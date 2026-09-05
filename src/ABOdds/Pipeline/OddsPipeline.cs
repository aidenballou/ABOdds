using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Services.Calculations;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Pipeline;

public sealed class OddsPipeline(
    CalculationRepository calculations,
    AlertRepository alerts,
    IOptions<PollingOptions> polling,
    IOptions<FairValueOptions> references,
    IOptions<EvOptions> ev,
    SemaphoreSlim alertStateGate,
    IClock clock)
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        while (await calculations.GetNextPendingBatchAsync(cancellationToken) is { } id)
        {
            await ProcessAsync(id, cancellationToken);
        }
    }

    public async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var stage = "load";
        try
        {
            var batch = await calculations.LoadBatchQuotesAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Poll batch {id} is missing.");
            stage = "fair-value";
            var fair = FairValueCalculator.Calculate(batch.Quotes, batch.ObservedAtUtc,
                polling.Value.MaximumSourceAge, references.Value);
            await calculations.SaveFairValuesAsync(id, fair, clock.UtcNow, cancellationToken);
            stage = "ev-detection";
            var persisted = await calculations.LoadFairValuesAsync(id, cancellationToken);
            var opportunities = EvScanner.Scan(batch.Quotes, persisted, batch.ObservedAtUtc,
                polling.Value.MaximumSourceAge, ev.Value);
            await calculations.SaveEvOpportunitiesAsync(id, opportunities, clock.UtcNow, cancellationToken);
            stage = "alert-rules";
            var alertBatch = await calculations.LoadAlertBatchAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Poll batch {id} is missing.");
            await alertStateGate.WaitAsync(cancellationToken);
            try { await alerts.ApplyRulesAsync(alertBatch with { ProcessedAtUtc = clock.UtcNow }, cancellationToken); }
            finally { alertStateGate.Release(); }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await calculations.RecordFailureAsync(id, stage, exception, clock.UtcNow, cancellationToken);
            throw;
        }
    }
}
