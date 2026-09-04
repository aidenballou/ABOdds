using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Services.Calculations;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed class FairValueWorker : RecoveringBatchWorker
{
    private readonly CalculationRepository _repository;
    private readonly PipelineChannels _channels;
    private readonly PollingOptions _pollingOptions;
    private readonly FairValueOptions _fairValueOptions;

    public FairValueWorker(
        CalculationRepository repository,
        PipelineChannels channels,
        IOptions<PollingOptions> pollingOptions,
        IOptions<FairValueOptions> fairValueOptions,
        IClock clock,
        ILogger<FairValueWorker> logger)
        : base(channels.FairValueBatches.Reader, repository, clock, logger)
    {
        _repository = repository;
        _channels = channels;
        _pollingOptions = pollingOptions.Value;
        _fairValueOptions = fairValueOptions.Value;
    }

    protected override string StageName => "fair-value";

    protected override Task<Guid?> GetNextPendingBatchAsync(CancellationToken cancellationToken) =>
        _repository.GetNextFairValueBatchAsync(cancellationToken);

    protected override async Task ProcessBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await _repository.LoadBatchQuotesAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return;
        }

        var fairValues = FairValueCalculator.Calculate(
            batch.Quotes,
            batch.ObservedAtUtc,
            _pollingOptions.MaximumSourceAge,
            _fairValueOptions);
        var completed = await _repository.SaveFairValuesAsync(
            batchId,
            fairValues,
            batch.ObservedAtUtc,
            cancellationToken);
        if (completed)
        {
            await _channels.EvBatches.Writer.WriteAsync(batchId, cancellationToken);
        }
    }
}
