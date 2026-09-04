using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Time;

namespace ABOdds.Workers;

public sealed class AlertRuleWorker : RecoveringBatchWorker
{
    private readonly CalculationRepository _calculationRepository;
    private readonly AlertRepository _alertRepository;
    private readonly PipelineChannels _channels;
    private readonly SemaphoreSlim _alertStateGate;

    public AlertRuleWorker(
        CalculationRepository calculationRepository,
        AlertRepository alertRepository,
        PipelineChannels channels,
        SemaphoreSlim alertStateGate,
        IClock clock,
        ILogger<AlertRuleWorker> logger)
        : base(channels.AlertBatches.Reader, calculationRepository, clock, logger)
    {
        _calculationRepository = calculationRepository;
        _alertRepository = alertRepository;
        _channels = channels;
        _alertStateGate = alertStateGate;
    }

    protected override string StageName => "alert-rules";

    protected override Task<Guid?> GetNextPendingBatchAsync(CancellationToken cancellationToken) =>
        _calculationRepository.GetNextAlertBatchAsync(cancellationToken);

    protected override async Task ProcessBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await _calculationRepository.LoadAlertBatchAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return;
        }

        IReadOnlyList<Guid> alertIds;
        await _alertStateGate.WaitAsync(cancellationToken);
        try
        {
            alertIds = await _alertRepository.ApplyRulesAsync(batch, cancellationToken);
        }
        finally
        {
            _alertStateGate.Release();
        }

        foreach (var alertId in alertIds)
        {
            _channels.AlertOutbox.Writer.TryWrite(alertId);
        }
    }
}
