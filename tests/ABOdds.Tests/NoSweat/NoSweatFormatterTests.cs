using ABOdds.NoSweat;

namespace ABOdds.Tests.NoSweat;

public sealed class NoSweatFormatterTests
{
    [Fact]
    public void QualifyingReportLabelsProjectionAndIncludesBothCashPathsAndConversionFunding()
    {
        var plans = NoSweatOptimizer.FindQualifying([NoSweatTestData.Market()], NoSweatTestData.Options);
        var report = NoSweatFormatter.Qualifying(plans, TimeSpan.FromSeconds(90));
        var message = Assert.Single(report.Pages);
        Assert.Contains("Projected minimum profit: $375.00", message, StringComparison.Ordinal);
        Assert.Contains("Qualify: $1,500.00 on BetMGM", message, StringComparison.Ordinal);
        Assert.Contains("Initial hedge: $1,125.00 on fanduel", message, StringComparison.Ordinal);
        Assert.Contains("total bankroll required: $2,625.00", message, StringComparison.Ordinal);
        Assert.Contains("Qualifying win: $375.00 profit", message, StringComparison.Ordinal);
        Assert.Contains("Projected conversion: $750.00 (50.00", message, StringComparison.Ordinal);
        Assert.Contains("Projected loss-path profit: $375.00", message, StringComparison.Ordinal);
        Assert.Contains("Conversion hedge cash: $750.00", message, StringComparison.Ordinal);
        Assert.Contains("cannot be locked now", message, StringComparison.Ordinal);
        Assert.Contains("Home +3.5 +100 / 2", message, StringComparison.Ordinal);
        Assert.Contains("Away -3.5 +100 / 2", message, StringComparison.Ordinal);
        Assert.Equal(NoSweatTestData.Now.AddSeconds(90), report.ValidUntilUtc);
    }

    [Fact]
    public void TopFiveReportsArePagedBelowTheDiscordEmbedLimit()
    {
        var markets = Enumerable.Range(1, 5).Select(index =>
        {
            var market = NoSweatTestData.Market(id: $"event-{index}");
            return market with { Event = market.Event with { HomeTeam = new string('H', 160), AwayTeam = new string('A', 160) } };
        }).ToArray();
        var plans = NoSweatOptimizer.FindQualifying(markets, NoSweatTestData.Options);
        var report = NoSweatFormatter.Qualifying(plans, TimeSpan.FromSeconds(90));
        Assert.True(report.Pages.Count > 1);
        Assert.All(report.Pages, page => Assert.True(page.Length <= 3900));
        for (var rank = 1; rank <= 5; rank++)
            Assert.Single(report.Pages, page => page.Contains($"**#{rank} Projected minimum profit", StringComparison.Ordinal));
    }
}
