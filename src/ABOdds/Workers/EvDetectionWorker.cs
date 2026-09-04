using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Services.Calculations;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed class EvDetectionWorker : RecoveringBatchWorker
{
    private readonly CalculationRepository _repository;
    private readonly PipelineChannels _channels;
    private readonly PollingOptions _pollingOptions;
    private readonly EvOptions _evOptions;

    public EvDetectionWorker(
        CalculationRepository repository,
        PipelineChannels channels,
        IOptions<PollingOptions> pollingOptions,
        IOptions<EvOptions> evOptions,
        IClock clock,
        ILogger<EvDetectionWorker> logger)
        : base(channels.EvBatches.Reader, repository, clock, logger)
    {
        _repository = repository;
        _channels = channels;
        _pollingOptions = pollingOptions.Value;
        _evOptions = evOptions.Value;
    }

    protected override string StageName => "ev-detection";

    protected override Task<Guid?> GetNextPendingBatchAsync(CancellationToken cancellationToken) =>
        _repository.GetNextEvBatchAsync(cancellationToken);

    protected override async Task ProcessBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await _repository.LoadBatchQuotesAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return;
        }

        var fairValues = await _repository.LoadFairValuesAsync(batchId, cancellationToken);
        var opportunities = EvScanner.Scan(
            batch.Quotes,
            fairValues,
            batch.ObservedAtUtc,
            _pollingOptions.MaximumSourceAge,
            _evOptions);
        var completed = await _repository.SaveEvOpportunitiesAsync(
            batchId,
            opportunities,
            batch.ObservedAtUtc,
            cancellationToken);
        if (completed)
        {
            await _channels.AlertBatches.Writer.WriteAsync(batchId, cancellationToken);
        }
    }
}
