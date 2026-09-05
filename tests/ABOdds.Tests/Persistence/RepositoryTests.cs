using ABOdds.Domain;
using ABOdds.Services.Alerts;
using ABOdds.Services.Calculations;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Tests.Persistence;

public sealed class RepositoryTests : PostgresTest
{
    [PostgresFact]
    public async Task ReferenceRoles_SurvivePersistenceAndLabelEachAlertCase()
    {
        foreach (var excludedBook in new[] { "none", "betonlineag", "pinnacle" })
        {
            var batch = Batch();
            var game = batch.Events.Single();
            batch = batch with { Events = [game with
            {
                Quotes = game.Quotes.Where(quote => quote.BookmakerKey != excludedBook).ToArray()
            }] };
            var stored = await ProcessAsync(batch);
            var opportunity = Assert.Single(stored.Opportunities).Opportunity;
            var message = DiscordAlertFormatter.Format(opportunity, Now);
            var expected = excludedBook switch
            {
                "betonlineag" => "UNVALIDATED",
                "pinnacle" => "LOWER confidence",
                _ => "BetOnline confirmed"
            };
            Assert.Contains(expected, message, StringComparison.Ordinal);
            Assert.DoesNotContain(opportunity.Sources, source => source.BookmakerKey == "lowvig");
        }
    }

    [PostgresFact]
    public async Task ValidationBoundary_UsesUnroundedSourceProbabilitiesAfterPersistence()
    {
        var id = await Ingestion.SaveAsync(Batch(), default);
        var quotes = (await Calculations.LoadBatchQuotesAsync(id, default))!.Quotes;
        var quote = quotes.Single(value => value.BookmakerKey == "fanduel");
        var probability = 2m / 3m;
        var fair = new CalculatedFairValue(quote.MarketId, quote.SelectionKey, quote.SelectionDisplayName,
            quote.Line, probability, 1m / probability,
            [
                new("pinnacle", "Pinnacle", 1.5m, probability, 1m, Now) { Role = FairValueSourceRole.Primary },
                new("betonlineag", "BetOnline", 1.5m, probability - 0.015m, 0m, Now) { Role = FairValueSourceRole.Validation }
            ]);
        await Calculations.SaveFairValuesAsync(id, [fair], Now, default);
        var stored = await Calculations.LoadFairValuesAsync(id, default);
        Assert.Single(EvScanner.Scan(quotes, stored, Now, TimeSpan.FromSeconds(90), Ev));
    }

    [PostgresFact]
    public async Task ProductionRetryConfiguration_PersistsPipelineAndReplaysWithoutDuplicates()
    {
        var batch = await ProcessAsync(Batch());
        var alertId = Assert.Single(await Alerts.ApplyRulesAsync(batch, default));
        Assert.False(await Calculations.SaveFairValuesAsync(batch.BatchId, [], Now, default));
        Assert.False(await Calculations.SaveEvOpportunitiesAsync(batch.BatchId, [], Now, default));
        Assert.Empty(await Alerts.ApplyRulesAsync(batch, default));
        var pending = await Alerts.GetNextPendingAsync(Now, default);
        Assert.Equal(alertId, pending!.Id);
        await Alerts.MarkSentAsync(alertId, Now.AddSeconds(1), default);
        Assert.Null(await Alerts.GetNextPendingAsync(Now.AddSeconds(2), default));

        await using var db = await Factory.CreateDbContextAsync();
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal(5, await db.OddsSnapshots.CountAsync());
        Assert.Equal(2, await db.FairValues.CountAsync());
        Assert.Equal(1, await db.EvOpportunities.CountAsync());
        Assert.Equal(1, await db.Alerts.CountAsync());
        Assert.Equal(AlertDeliveryStatus.Sent, (await db.Alerts.SingleAsync()).DeliveryStatus);
        var saved = await db.PollBatches.SingleAsync();
        Assert.NotNull(saved.FairValueCompletedAtUtc);
        Assert.NotNull(saved.EvCompletedAtUtc);
        Assert.NotNull(saved.AlertRulesCompletedAtUtc);
    }

    [PostgresFact]
    public async Task InvalidIngestion_RollsBackSnapshotAndEventUpdateTogether()
    {
        await Ingestion.SaveAsync(Batch(), default);
        var batch = Batch(Now.AddMinutes(1));
        var game = batch.Events.Single();
        var invalid = game.Quotes[0] with { SelectionKey = new string('x', 201) };
        batch = batch with { Events = [game with { CommenceTimeUtc = Now.AddHours(3), Quotes = [invalid] }] };
        await Assert.ThrowsAsync<DbUpdateException>(() => Ingestion.SaveAsync(batch, default));

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.PollBatches.CountAsync());
        Assert.Equal(5, await db.OddsSnapshots.CountAsync());
        Assert.Equal(Now.AddHours(2), (await db.Events.SingleAsync()).CommenceTimeUtc);
    }

    [PostgresFact]
    public async Task InvalidFairValue_DoesNotMarkBatchCompleted()
    {
        var id = await Ingestion.SaveAsync(Batch(), default);
        var invalid = new CalculatedFairValue(Guid.NewGuid(), "over", "Over", 44.5m, 0.5m, 2m, []);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Calculations.SaveFairValuesAsync(id, [invalid], Now, default));

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Null((await db.PollBatches.SingleAsync()).FairValueCompletedAtUtc);
        Assert.Equal(0, await db.FairValues.CountAsync());
    }
}
