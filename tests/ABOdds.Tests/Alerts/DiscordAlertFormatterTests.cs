using ABOdds.Domain;
using ABOdds.Services.Alerts;

namespace ABOdds.Tests.Alerts;

public sealed class DiscordAlertFormatterTests
{
    [Theory]
    [InlineData(FairValueSourceRole.Primary, "Pinnacle no-vig", "UNVALIDATED")]
    [InlineData(FairValueSourceRole.Fallback, "BetOnline no-vig", "LOWER confidence")]
    public void Format_LabelsSingleReferenceConfidence(FairValueSourceRole role, string basis, string status)
    {
        var opportunity = TestOpportunity.Create();
        opportunity = opportunity with { Sources = [opportunity.Sources[0] with { Role = role }] };
        var message = DiscordAlertFormatter.Format(opportunity, opportunity.CommenceTimeUtc.AddMinutes(-5));
        Assert.Contains(basis, message, StringComparison.Ordinal);
        Assert.Contains(status, message, StringComparison.Ordinal);
        Assert.DoesNotContain("BetOnline confirmed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LabelsValidatedPinnacleConcisely()
    {
        var opportunity = TestOpportunity.Create(decimalOdds: 2m);
        opportunity = opportunity with
        {
            Sources =
        [
            opportunity.Sources[0] with { Role = FairValueSourceRole.Primary, NoVigProbability = 0.55m },
            opportunity.Sources[1] with { Role = FairValueSourceRole.Validation, NoVigProbability = 0.54m }
        ]
        };
        var message = DiscordAlertFormatter.Format(opportunity, opportunity.CommenceTimeUtc.AddMinutes(-5));
        Assert.Contains("Pinnacle no-vig", message, StringComparison.Ordinal);
        Assert.Contains("BetOnline confirmed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Reference EV difference", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("spreads", 3)]
    [InlineData("spreads", -3)]
    [InlineData("spreads", 0)]
    [InlineData("totals", 44)]
    [InlineData("h2h", 0)]
    public void Format_PushCapableMarket_LabelsConditionalEvAndProbability(string market, int line)
    {
        var opportunity = TestOpportunity.Create(marketKey: market, line: market == "h2h" ? null : line);
        var message = DiscordAlertFormatter.Format(opportunity, opportunity.CommenceTimeUtc.AddMinutes(-5));
        Assert.Contains("**+7.4% EV**", message, StringComparison.Ordinal);
        Assert.Contains("EV and win probability exclude pushes.", message, StringComparison.Ordinal);
        Assert.Contains("Push probability is not estimated.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_TotalsFromDifferentGames_IdentifiesEachMatchupAndKickoff()
    {
        var first = TestOpportunity.Create(line: 44.5m, marketKey: MarketKeys.Total) with
        {
            SelectionKey = "over",
            SelectionDisplayName = "Over"
        };
        var second = first with
        {
            SportKey = "americanfootball_ncaaf",
            HomeTeam = "Purdue",
            AwayTeam = "Indiana",
            CommenceTimeUtc = first.CommenceTimeUtc.AddHours(1)
        };
        var firstMessage = DiscordAlertFormatter.Format(first, first.CommenceTimeUtc.AddMinutes(-5));
        var secondMessage = DiscordAlertFormatter.Format(second, second.CommenceTimeUtc.AddMinutes(-5));
        Assert.Contains("Indianapolis Colts @ Houston Texans", firstMessage, StringComparison.Ordinal);
        Assert.Contains("NFL | Total", firstMessage, StringComparison.Ordinal);
        Assert.Contains("Indiana @ Purdue", secondMessage, StringComparison.Ordinal);
        Assert.Contains("NCAAF | Total", secondMessage, StringComparison.Ordinal);
        Assert.Contains($"Kickoff: <t:{second.CommenceTimeUtc.ToUnixTimeSeconds()}:f>", secondMessage, StringComparison.Ordinal);
        Assert.Contains("**Over 44.5**", firstMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Over +44.5", firstMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("exclude pushes", firstMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SpreadOpportunity_IncludesBetDetailsSourcesAndNativeTimestamp()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 4, 2, 17, 32, TimeSpan.Zero);
        var opportunity = TestOpportunity.Create(sourceUpdatedAtUtc: updatedAt);
        var message = DiscordAlertFormatter.Format(opportunity, updatedAt);

        Assert.StartsWith("**Colts +3.5** · **-105**\nFanDuel · **+7.4% EV**", message, StringComparison.Ordinal);
        Assert.True(message.Length < 600);
        Assert.DoesNotContain("exclude pushes", message, StringComparison.Ordinal);
        Assert.DoesNotContain("+EV BET", message, StringComparison.Ordinal);
        Assert.Contains("Fair **-122**", message, StringComparison.Ordinal);
        Assert.Contains("Win probability 55.0%", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Reference prices", message, StringComparison.Ordinal);
        Assert.EndsWith($"Updated <t:{updatedAt.ToUnixTimeSeconds()}:R>", message, StringComparison.Ordinal);
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
