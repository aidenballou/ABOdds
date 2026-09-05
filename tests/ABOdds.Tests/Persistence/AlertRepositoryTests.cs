using ABOdds.Domain;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Tests.Persistence;

public sealed class AlertRepositoryTests : PostgresTest
{
    [PostgresFact]
    public async Task FailedReappearance_AllowsFreshReplacementWithoutRepeatedAlerts()
    {
        await AssertReappearanceReplacementAsync(expire: false);
    }

    [PostgresFact]
    public async Task ExpiredReappearance_AllowsFreshReplacementWithoutRepeatedAlerts()
    {
        await AssertReappearanceReplacementAsync(expire: true);
    }

    [PostgresFact]
    public async Task Reconciliation_UpdatesLastSeenWithoutMovingTheAlertBaselineUntilImprovement()
    {
        var first = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default));
        await Alerts.MarkSentAsync(first, Now.AddSeconds(1), default);
        await using var db = await Factory.CreateDbContextAsync();
        var baseline = (await db.AlertStates.AsNoTracking().SingleAsync()).LastAlertedExpectedValue;

        Assert.Empty(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddSeconds(10), 2.01m)), default));
        var seen = await db.AlertStates.AsNoTracking().SingleAsync();
        Assert.True(seen.IsActive);
        Assert.Equal(Now.AddSeconds(10), seen.LastSeenAtUtc);
        Assert.Equal(2.01m, seen.LastSeenDecimalOdds);
        Assert.True(seen.LastSeenExpectedValue > baseline);
        Assert.Equal(baseline, seen.LastAlertedExpectedValue);
        Assert.Equal(Now, seen.LastAlertedAtUtc);

        var improved = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddSeconds(20), 2.02m)), default));
        var alerted = await db.AlertStates.AsNoTracking().SingleAsync();
        Assert.Equal(AlertReason.Improved, (await db.Alerts.SingleAsync(value => value.Id == improved)).Reason);
        Assert.Equal(alerted.LastSeenExpectedValue, alerted.LastAlertedExpectedValue);
        Assert.Equal(Now.AddSeconds(20), alerted.LastAlertedAtUtc);
        Assert.Empty(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddSeconds(30), 2.02m)), default));
        Assert.Equal(2, await db.Alerts.CountAsync());
    }

    [PostgresFact]
    public async Task Reconciliation_KeepsExactLinesAndEventsIndependent()
    {
        var batch = Batch();
        var game = batch.Events[0];
        var alternate = game.Quotes.Select(quote => quote with { Line = quote.Line > 0 ? 4.5m : -4.5m });
        game = game with { Quotes = game.Quotes.Concat(alternate).ToArray() };
        batch = batch with { Events = [game, game with { ProviderEventId = "event-2" }] };
        var processed = await ProcessAsync(batch);
        Assert.Equal(4, (await Alerts.ApplyRulesAsync(processed, default)).Count);
        Assert.Empty(await Alerts.ApplyRulesAsync(processed, default));

        await using var db = await Factory.CreateDbContextAsync();
        var states = await db.AlertStates.AsNoTracking().ToListAsync();
        Assert.Equal(4, states.Count);
        Assert.Equal(2, states.Select(state => state.EventId).Distinct().Count());
        foreach (var gameStates in states.GroupBy(state => state.EventId))
        {
            Assert.Collection(gameStates.Select(state => state.LineKey).Order(),
                line => Assert.Equal("3.5", line), line => Assert.Equal("4.5", line));
        }
    }

    private async Task AssertReappearanceReplacementAsync(bool expire)
    {
        var first = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default));
        await Alerts.MarkSentAsync(first, Now.AddSeconds(1), default);
        Assert.Empty(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddMinutes(1), 1.5m)), default));
        var returned = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddMinutes(2))), default));
        if (expire) await Alerts.MarkExpiredAsync(returned, "synthetic expiry", default);
        else await Alerts.MarkFailedAsync(returned, "synthetic failure", default);
        var replacement = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddMinutes(3))), default));
        Assert.Empty(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddMinutes(4))), default));
        await Alerts.MarkSentAsync(replacement, Now.AddMinutes(4), default);
        Assert.Empty(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch(Now.AddMinutes(5))), default));

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(AlertReason.Reappeared, (await db.Alerts.SingleAsync(value => value.Id == replacement)).Reason);
        Assert.Equal(3, await db.Alerts.CountAsync());
    }
}
