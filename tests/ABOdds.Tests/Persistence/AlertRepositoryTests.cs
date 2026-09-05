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
