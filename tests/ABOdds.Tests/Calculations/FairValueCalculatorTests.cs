using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Calculations;

namespace ABOdds.Tests.Calculations;

public sealed class FairValueCalculatorTests
{
    private static readonly TimeSpan MaximumSourceAge = TimeSpan.FromSeconds(90);

    [Fact]
    public void Calculate_UsesPinnacleProbabilityWithoutBlendingBetOnline()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 2m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        var teamA = Assert.Single(result, value => value.SelectionKey == "team a");
        Assert.InRange(teamA.FairProbability, 0.549999999m, 0.550000001m);
        Assert.InRange(teamA.FairDecimalOdds, 1.818181817m, 1.818181819m);
        Assert.Equal(2, teamA.Sources.Count);

        var pinnacle = Assert.Single(teamA.Sources, source => source.BookmakerKey == "pinnacle");
        Assert.InRange(pinnacle.NoVigProbability, 0.549999999m, 0.550000001m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Calculate_RejectsThreeWayMoneylines(bool staleDraw)
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Draw", 4m,
                sourceUpdatedAtUtc: staleDraw ? CalculationTestData.Now.AddSeconds(-91) : CalculationTestData.Now),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Draw", 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_DoesNotMixTwoWayAndThreeWayMoneylines()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Draw", 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        var home = Assert.Single(result, value => value.SelectionKey == "team a");
        Assert.InRange(home.FairProbability, 0.549999999m, 0.550000001m);
        Assert.Equal(FairValueSourceRole.Primary, Assert.Single(home.Sources).Role);
    }

    [Fact]
    public void Calculate_KeepsDifferentReferenceLinesUnvalidatedOrFallback()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team A", 1.91m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team B", 1.91m, 3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "betonlineag", "Team A", 1.91m, -4m),
            CalculationTestData.Quote(MarketKeys.Spread, "betonlineag", "Team B", 1.91m, 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(4, result.Count);
        Assert.All(result, value => Assert.Single(value.Sources));
        Assert.All(result.Where(value => Math.Abs(value.Line!.Value) == 3.5m),
            value => Assert.Equal(FairValueSourceRole.Primary, value.Sources[0].Role));
        Assert.All(result.Where(value => Math.Abs(value.Line!.Value) == 4m),
            value => Assert.Equal(FairValueSourceRole.Fallback, value.Sources[0].Role));
    }

    [Fact]
    public void Calculate_ProducesSpreadFairValuesForAnExactSharedLine()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team A", 1.8m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team B", 2.2m, 3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "betonlineag", "Team A", 1.8m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "betonlineag", "Team B", 2.2m, 3.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        var favorite = Assert.Single(result, value => value.SelectionKey == "team a");
        Assert.Equal(-3.5m, favorite.Line);
        Assert.InRange(favorite.FairProbability, 0.549999999m, 0.550000001m);
    }

    [Fact]
    public void Calculate_DevigsTotalsAtTheSamePoint()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Total, "pinnacle", "Over", 1.8m, 44.5m),
            CalculationTestData.Quote(MarketKeys.Total, "pinnacle", "Under", 2.2m, 44.5m),
            CalculationTestData.Quote(MarketKeys.Total, "betonlineag", "Over", 1.8m, 44.5m),
            CalculationTestData.Quote(MarketKeys.Total, "betonlineag", "Under", 2.2m, 44.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        var over = Assert.Single(result, value => value.SelectionKey == "over");
        Assert.Equal(44.5m, over.Line);
        Assert.InRange(over.FairProbability, 0.549999999m, 0.550000001m);
    }

    [Fact]
    public void Calculate_DropsAReferenceBookWhenEitherOutcomeIsStale()
    {
        var stale = CalculationTestData.Now - MaximumSourceAge - TimeSpan.FromSeconds(1);
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 1.8m, sourceUpdatedAtUtc: stale),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 2.2m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        Assert.All(result, value => Assert.Equal(FairValueSourceRole.Primary, Assert.Single(value.Sources).Role));
    }

    [Fact]
    public void Calculate_AllowsPinnacleWithoutValidation()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "not-a-reference", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "not-a-reference", "Team B", 2.2m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Equal(2, result.Count);
        Assert.All(result, value => Assert.Equal(FairValueSourceRole.Primary, Assert.Single(value.Sources).Role));
    }

    [Fact]
    public void Calculate_IgnoresLowVigEntirely()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.5m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.25m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "lowvig", "Team A", 2.25m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "lowvig", "Team B", 1.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        var teamA = Assert.Single(result, value => value.SelectionKey == "team a");
        var expected = 0.6m;
        Assert.InRange(teamA.FairProbability, expected - 0.000000001m, expected + 0.000000001m);
        Assert.Equal("pinnacle", Assert.Single(teamA.Sources).BookmakerKey);
    }

    [Theory]
    [InlineData("live", 1)]
    [InlineData(MarketPeriods.Pregame, -1)]
    public void Calculate_ExcludesNonPregameOrStartedMarkets(string period, int commenceOffsetMinutes)
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(
                MarketKeys.Moneyline,
                "pinnacle",
                "Team A",
                1.8m,
                period: period,
                commenceTimeUtc: CalculationTestData.Now.AddMinutes(commenceOffsetMinutes)),
            CalculationTestData.Quote(
                MarketKeys.Moneyline,
                "pinnacle",
                "Team B",
                2.2m,
                period: period,
                commenceTimeUtc: CalculationTestData.Now.AddMinutes(commenceOffsetMinutes))
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options());

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("incomplete")]
    public void Calculate_FallsBackToBetOnlineWhenPinnacleIsUnavailable(string reason)
    {
        var quotes = new List<MarketQuote>
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 2.2m)
        };
        if (reason != "missing")
            quotes.Add(CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m));
        if (reason == "stale")
            quotes.Add(CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2m,
                sourceUpdatedAtUtc: CalculationTestData.Now.AddSeconds(-91)));

        var result = FairValueCalculator.Calculate(quotes, CalculationTestData.Now, MaximumSourceAge, Options());

        Assert.Equal(2, result.Count);
        Assert.All(result, value => Assert.Equal(FairValueSourceRole.Fallback, Assert.Single(value.Sources).Role));
        Assert.InRange(result.Single(value => value.SelectionKey == "team a").FairProbability, 0.549999999m, 0.550000001m);
    }

    [Fact]
    public void Calculate_RequiresAtLeastOneCompleteReference()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "lowvig", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "lowvig", "Team B", 2m)
        };
        Assert.Empty(FairValueCalculator.Calculate(quotes, CalculationTestData.Now, MaximumSourceAge, Options()));
    }

    [Fact]
    public void Calculate_SourceFreshnessIncludesTheOpposingOutcome()
    {
        var oldest = CalculationTestData.Now.AddSeconds(-80);
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2m, sourceUpdatedAtUtc: oldest)
        };
        var result = FairValueCalculator.Calculate(quotes, CalculationTestData.Now, MaximumSourceAge, Options());
        Assert.Equal(2, result.Count);
        Assert.All(result, value => Assert.Equal(oldest, Assert.Single(value.Sources).SourceUpdatedAtUtc));
    }

    [Theory]
    [InlineData(MarketKeys.Spread, "Team A", "Team B", 3.5, 3.5)]
    [InlineData(MarketKeys.Spread, "Team A", "Unknown", 3.5, -3.5)]
    [InlineData(MarketKeys.Total, "Over", "Under", 44.5, 45.5)]
    [InlineData(MarketKeys.Total, "Over", "Unknown", 44.5, 44.5)]
    public void Calculate_RejectsIncompleteOrMismatchedReferencePairs(
        string market, string first, string second, decimal firstLine, decimal secondLine)
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(market, "pinnacle", first, 2m, firstLine),
            CalculationTestData.Quote(market, "pinnacle", second, 2m, secondLine)
        };
        Assert.Empty(FairValueCalculator.Calculate(quotes, CalculationTestData.Now, MaximumSourceAge, Options()));
    }

    private static FairValueOptions Options() => new()
    {
        ReferenceBooks = [new() { Key = "pinnacle" }, new() { Key = "betonlineag" }]
    };
}
