using ABOdds.Domain;
using ABOdds.Services.Alerts;

namespace ABOdds.Tests.Alerts;

public sealed class DiscordAlertFormatterTests
{
    [Fact]
    public void Format_SpreadOpportunity_IncludesBetDetailsSourcesAndNativeTimestamp()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 4, 2, 17, 32, TimeSpan.Zero);
        var opportunity = TestOpportunity.Create(sourceUpdatedAtUtc: updatedAt);
        var message = DiscordAlertFormatter.Format(opportunity, updatedAt);

        Assert.Contains("🟢 **+EV BET**", message, StringComparison.Ordinal);
        Assert.Contains("**Colts +3.5**", message, StringComparison.Ordinal);
        Assert.Contains("FanDuel: **-105**", message, StringComparison.Ordinal);
        Assert.Contains("Fair Odds: **-122**", message, StringComparison.Ordinal);
        Assert.Contains("Fair Probability: **55.0%**", message, StringComparison.Ordinal);
        Assert.Contains("EV: **+7.4%**", message, StringComparison.Ordinal);
        Assert.Contains("Sharp consensus:\nPinnacle -121\nBetOnline -123", message, StringComparison.Ordinal);
        Assert.EndsWith($"Updated: <t:{updatedAt.ToUnixTimeSeconds()}:T>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_TotalOpportunity_DoesNotPrefixTotalWithPlusSign()
    {
        var opportunity = TestOpportunity.Create(
            line: 44.5m,
            marketKey: MarketKeys.Total);

        var message = DiscordAlertFormatter.Format(
            opportunity,
            new DateTimeOffset(2026, 9, 4, 2, 17, 32, TimeSpan.Zero));

        Assert.Contains("**Colts 44.5**", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Colts +44.5", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDecimal_ConvertsAndRoundsAmericanOdds()
    {
        Assert.Equal(100, AmericanOdds.FromDecimal(2m));
        Assert.Equal(150, AmericanOdds.FromDecimal(2.5m));
        Assert.Equal(-200, AmericanOdds.FromDecimal(1.5m));
        Assert.Equal(-105, AmericanOdds.FromDecimal(1.9523809524m));
        Assert.Equal("+100", AmericanOdds.Format(2m));
    }

    [Fact]
    public void FromDecimal_OddsAtOrBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmericanOdds.FromDecimal(1m));
    }
}
