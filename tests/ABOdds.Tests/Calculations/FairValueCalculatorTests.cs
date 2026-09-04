using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Calculations;

namespace ABOdds.Tests.Calculations;

public sealed class FairValueCalculatorTests
{
    private static readonly TimeSpan MaximumSourceAge = TimeSpan.FromSeconds(90);

    [Fact]
    public void Calculate_RemovesVigBeforeWeightingMoneylineProbabilities()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team B", 2m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(
                minimumBooks: 2,
                ("pinnacle", 0.5m),
                ("circa", 0.3m)));

        Assert.Equal(2, result.Count);
        var teamA = Assert.Single(result, value => value.SelectionKey == "team a");
        Assert.InRange(teamA.FairProbability, 0.531249999m, 0.531250001m);
        Assert.InRange(teamA.FairDecimalOdds, 1.882352940m, 1.882352942m);
        Assert.Equal(2, teamA.Sources.Count);

        var pinnacle = Assert.Single(teamA.Sources, source => source.BookmakerKey == "pinnacle");
        Assert.InRange(pinnacle.NoVigProbability, 0.549999999m, 0.550000001m);
    }

    [Fact]
    public void Calculate_DevigsEveryOutcomeInAThreeWayMoneyline()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Draw", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Draw", 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

        Assert.Equal(3, result.Count);
        var home = Assert.Single(result, value => value.SelectionKey == "team a");
        var draw = Assert.Single(result, value => value.SelectionKey == "draw");
        Assert.Equal(0.5m, home.FairProbability);
        Assert.Equal(0.25m, draw.FairProbability);
        Assert.All(result, value => Assert.Equal(2, value.Sources.Count));
    }

    [Fact]
    public void Calculate_DoesNotMixTwoWayAndThreeWayMoneylines()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Draw", 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_RejectsSpreadConsensusWhenReferenceLinesDiffer()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team A", 1.91m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team B", 1.91m, 3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "circa", "Team A", 1.91m, -4m),
            CalculationTestData.Quote(MarketKeys.Spread, "circa", "Team B", 1.91m, 4m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_ProducesSpreadFairValuesForAnExactSharedLine()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team A", 1.8m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "pinnacle", "Team B", 2.2m, 3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "circa", "Team A", 1.8m, -3.5m),
            CalculationTestData.Quote(MarketKeys.Spread, "circa", "Team B", 2.2m, 3.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

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
            CalculationTestData.Quote(MarketKeys.Total, "circa", "Over", 1.8m, 44.5m),
            CalculationTestData.Quote(MarketKeys.Total, "circa", "Under", 2.2m, 44.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

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
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team A", 1.8m, sourceUpdatedAtUtc: stale),
            CalculationTestData.Quote(MarketKeys.Moneyline, "circa", "Team B", 2.2m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_RequiresConfiguredReferenceBookQuorum()
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
            Options(minimumBooks: 2, ("pinnacle", 0.5m), ("circa", 0.5m)));

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_RenormalizesWeightsWhenAConfiguredBookIsMissing()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.5m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.25m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "bookmaker", "Team A", 2.25m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "bookmaker", "Team B", 1.5m)
        };

        var result = FairValueCalculator.Calculate(
            quotes,
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(
                minimumBooks: 2,
                ("pinnacle", 0.5m),
                ("circa", 0.3m),
                ("bookmaker", 0.2m)));

        var teamA = Assert.Single(result, value => value.SelectionKey == "team a");
        var expected = (0.6m * 0.5m + 0.4m * 0.2m) / 0.7m;
        Assert.InRange(teamA.FairProbability, expected - 0.000000001m, expected + 0.000000001m);
        Assert.Equal(2, teamA.Sources.Count);
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
            Options(minimumBooks: 1, ("pinnacle", 1m)));

        Assert.Empty(result);
    }

    private static FairValueOptions Options(
        int minimumBooks,
        params (string Key, decimal Weight)[] books) =>
        new()
        {
            MinimumReferenceBooks = minimumBooks,
            ReferenceBooks = books
                .Select(book => new ReferenceBookOptions
                {
                    Key = book.Key,
                    DisplayName = book.Key,
                    Weight = book.Weight
                })
                .ToList()
        };
}
