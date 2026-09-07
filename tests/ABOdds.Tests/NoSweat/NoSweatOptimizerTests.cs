using ABOdds.NoSweat;

namespace ABOdds.Tests.NoSweat;

public sealed class NoSweatOptimizerTests
{
    [Fact]
    public void BonusExample_ConvertsThreeHundredAtPlusSixHundredWithA1560Hedge()
    {
        var result = NoSweatOptimizer.Convert(NoSweatTestData.Market(7m, 15m / 13m), 300m, 7000m);
        Assert.Equal(1560m, result.HedgeAmount);
        Assert.InRange(result.CashValue, 239.99m, 240m);
        Assert.InRange(result.ConversionRate, 0.7999m, 0.8m);
        Assert.Equal(240m, result.CashIfBonusWins);
    }

    [Fact]
    public void BonusStakeIsNotReturned_AndCashConstraintCapsTheHedge()
    {
        var result = NoSweatOptimizer.Convert(NoSweatTestData.Market(7m, 1.2m), 300m, 500m);
        Assert.Equal(500m, result.HedgeAmount);
        Assert.Equal(1300m, result.CashIfBonusWins);
        Assert.Equal(100m, result.CashIfHedgeWins);
        Assert.Equal(100m, result.CashValue);
        Assert.Equal(300m, result.BonusAmount);
    }

    [Fact]
    public void QualifyingPathsIncludeTheLostCashStakeAndOnlyTheBonusWinnings()
    {
        var result = Assert.Single(NoSweatOptimizer.FindQualifying([NoSweatTestData.Market()], NoSweatTestData.Options));
        Assert.Equal(1500m, result.QualifyingStake);
        Assert.Equal(1125m, result.InitialHedge);
        Assert.Equal(750m, result.Conversion.CashValue);
        Assert.Equal(375m, result.WinPathProfit);
        Assert.Equal(375m, result.LossPathProfit);
        Assert.Equal(375m, result.ProjectedMinimumProfit);
        Assert.Equal(2625m, result.InitialBankrollRequired);
        Assert.Equal(6625m, result.LossPathCashBeforeConversion);
    }

    [Fact]
    public void ConversionFundingUsesTheLossPathCashAndMayReduceQualifyingStake()
    {
        var initial = NoSweatTestData.Market();
        var conversion = NoSweatTestData.Market(7m, 1.2m, "conversion");
        var result = Assert.Single(NoSweatOptimizer.FindQualifying([initial, conversion], NoSweatTestData.Options),
            value => value.QualifyingMarket.Event.ProviderEventId == "event-1");
        Assert.True(result.Conversion.HedgeAmount <= result.LossPathCashBeforeConversion);
        Assert.True(result.BankrollRequired <= 7000m);
        Assert.True(result.QualifyingStake <= 1500m);
        Assert.Equal("conversion", result.Conversion.Market.Event.ProviderEventId);
        Assert.True(result.ProjectedMinimumProfit > 375m);
        Assert.Equal(result.LossPathCashBeforeConversion - 7000m + result.Conversion.CashValue, result.LossPathProfit);
    }

    [Fact]
    public void RankingsUseCashConversionAndReturnOnlyFivePlans()
    {
        var markets = Enumerable.Range(0, 8).Select(index => NoSweatTestData.Market(2m + index / 10m, 2m, $"event-{index}")).ToArray();
        var results = NoSweatOptimizer.FindQualifying(markets, NoSweatTestData.Options);
        Assert.Equal(5, results.Count);
        Assert.Equal(results.Select(value => value.ProjectedMinimumProfit).OrderDescending(), results.Select(value => value.ProjectedMinimumProfit));
        Assert.All(results, value =>
        {
            Assert.InRange(value.QualifyingStake, 0.01m, 1500m);
            Assert.True(value.BankrollRequired <= 7000m);
            Assert.Equal(decimal.Round(value.InitialHedge, 2), value.InitialHedge);
            Assert.Equal(decimal.Round(value.Conversion.HedgeAmount, 2), value.Conversion.HedgeAmount);
        });
        var bonuses = NoSweatOptimizer.FindConversions([NoSweatTestData.Market(10m, 1.05m, "longest"),
            NoSweatTestData.Market(7m, 1.2m, "better")], 300m, 7000m);
        Assert.Equal("better", bonuses[0].Market.Event.ProviderEventId);
    }

    [Fact]
    public void EqualPriceMarketsKeepTheirDistinctSelectionsInTheTopFive()
    {
        var markets = Enumerable.Range(0, 20).Select(index => NoSweatTestData.Market(id: $"event-{index}")).ToArray();
        var results = NoSweatOptimizer.FindQualifying(markets, NoSweatTestData.Options);
        Assert.Equal(5, results.Count);
        Assert.Equal(5, results.Select(result => result.QualifyingMarket.Event.ProviderEventId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(375m, result.ProjectedMinimumProfit));
    }

    [Fact]
    public void MinimumQualifyingOddsDoNotRestrictBonusConversionBenchmarks()
    {
        var options = new NoSweatOptions { MinimumQualifyingDecimalOdds = 7m };
        var markets = new[] { NoSweatTestData.Market(2m, 10m, "conversion"), NoSweatTestData.Market(7m, 1.05m, "qualifying") };
        var result = Assert.Single(NoSweatOptimizer.FindQualifying(markets, options));
        Assert.Equal("qualifying", result.QualifyingMarket.Event.ProviderEventId);
        Assert.Equal("conversion", result.Conversion.Market.Event.ProviderEventId);
        Assert.Equal(2, NoSweatOptimizer.FindConversions(markets, 300m, 7000m).Count);
    }

    [Theory]
    [InlineData(2, 2, 7, 1.2)]
    [InlineData(3, 1.5, 4, 1.4)]
    [InlineData(1.9, 1.9, 2, 2)]
    [InlineData(2.1, 2.1, 7, 1.15)]
    [InlineData(2.88, 1.45, 8.54, 1.18)]
    [InlineData(2.21, 1.44, 2.27, 1.41)]
    [InlineData(15, 2, 10, 2)]
    public void OptimizerMatchesExhaustiveCentStakeSearchOnSmallBankrolls(decimal a, decimal b, decimal c, decimal d)
    {
        var markets = new[] { NoSweatTestData.Market(a, b), NoSweatTestData.Market(c, d, "conversion") };
        var options = new NoSweatOptions { PromotionLimit = 1m, Bankroll = 2m };
        var result = NoSweatOptimizer.FindQualifying(markets, options)
            .SingleOrDefault(value => value.QualifyingMarket.Event.ProviderEventId == "event-1");
        var expected = decimal.MinValue;
        for (var stake = 0.01m; stake <= 1m; stake += 0.01m)
        for (var hedge = 0m; hedge <= 2m - stake; hedge += 0.01m)
        {
            var win = Floor(stake * (a - 1m)) - hedge;
            var loss = -stake + Floor(hedge * (b - 1m));
            foreach (var conversion in markets)
            {
                var cash = 2m + loss;
                var bonus = NoSweatOptimizer.Convert(conversion, stake, cash);
                expected = Math.Max(expected, Math.Min(win, loss + bonus.CashValue));
            }
        }
        if (expected <= 0) Assert.Null(result);
        else Assert.Equal(expected, Assert.IsType<NoSweatOpportunity>(result).ProjectedMinimumProfit);
    }

    private static decimal Floor(decimal value) => decimal.Floor(value * 100m) / 100m;
}
