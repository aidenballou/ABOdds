using ABOdds.Configuration;
using ABOdds.Pipeline;
using ABOdds.Time;
using ABOdds.Services.Calculations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Persistence;

public sealed class HistoryTests : PostgresTest
{
    [PostgresFact]
    public async Task Reschedule_DoesNotRewriteEarlierQuoteOrOpportunityMetadata()
    {
        var first = await ProcessAsync(Batch());
        var next = Batch(Now.AddMinutes(1));
        next = next with { Events = [next.Events.Single() with { CommenceTimeUtc = Now.AddHours(3), HomeTeam = "Renamed home team" }] };
        await Ingestion.SaveAsync(next, default);
        var quotes = (await Calculations.LoadBatchQuotesAsync(first.BatchId, default))!.Quotes;
        Assert.Equal(5, quotes.Count);
        Assert.All(quotes, quote => Assert.Equal(Now.AddHours(2), quote.CommenceTimeUtc));
        Assert.All(quotes, quote => Assert.Equal("Houston Texans", quote.HomeTeam));
        var opportunity = Assert.Single((await Calculations.LoadAlertBatchAsync(first.BatchId, default))!.Opportunities).Opportunity;
        Assert.Equal(Now.AddHours(2), opportunity.CommenceTimeUtc);
        Assert.Equal("Houston Texans", opportunity.HomeTeam);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(Now.AddHours(3), (await db.Events.SingleAsync()).CommenceTimeUtc);
    }

    [PostgresFact]
    public async Task DelayedProcessing_RecordsActualTimesWithoutChangingObservationBaseline()
    {
        var id = await Ingestion.SaveAsync(Batch(), default);
        using var gate = new SemaphoreSlim(1, 1);
        var processed = Now.AddSeconds(20);
        var pipeline = new OddsPipeline(Calculations, Alerts, Options.Create(new PollingOptions()),
            Options.Create(References), Options.Create(Ev), gate, new FixedClock(processed));
        await pipeline.ProcessAsync(id, default);
        await using var db = await Factory.CreateDbContextAsync();
        var batch = await db.PollBatches.SingleAsync();
        Assert.Equal(Now, batch.ObservedAtUtc);
        Assert.NotNull(batch.EventMetadataJson);
        var fairValues = await db.FairValues.ToListAsync();
        Assert.Equal(2, fairValues.Count);
        Assert.All(fairValues, value =>
        {
            Assert.Equal(FairValueCalculator.Version, value.CalculationVersion);
            Assert.Equal(processed, value.CalculatedAtUtc);
        });
        Assert.Equal(processed, (await db.EvOpportunities.SingleAsync()).DetectedAtUtc);
        Assert.Equal(processed, batch.FairValueCompletedAtUtc);
        Assert.Equal(processed, batch.EvCompletedAtUtc);
        Assert.Equal(processed, batch.AlertRulesCompletedAtUtc);
        Assert.Equal(processed, (await db.Alerts.SingleAsync()).CreatedAtUtc);
        Assert.Equal(Now, (await db.AlertStates.SingleAsync()).LastAlertedAtUtc);
        var pending = await Alerts.GetNextPendingAsync(processed, default);
        Assert.NotNull(pending);
        await Alerts.MarkFailedAsync(pending.Id, "synthetic failure after delayed processing", default);
        Assert.Null(await db.AlertStates.Select(value => value.LastAlertedAtUtc).SingleAsync());
    }

    private sealed class FixedClock(DateTimeOffset time) : IClock { public DateTimeOffset UtcNow => time; }
}
