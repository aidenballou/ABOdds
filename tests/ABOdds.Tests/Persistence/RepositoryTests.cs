using ABOdds.Domain;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Tests.Persistence;

public sealed class RepositoryTests : PostgresTest
{
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
        Assert.Equal(7, await db.OddsSnapshots.CountAsync());
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
        Assert.Equal(7, await db.OddsSnapshots.CountAsync());
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
