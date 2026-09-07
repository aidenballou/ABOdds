using ABOdds.Domain;
using ABOdds.NoSweat;

namespace ABOdds.Tests.NoSweat;

public sealed class HedgeMarketMatcherTests
{
    [Fact]
    public void MatchesOppositeSpreadAndTotalSelectionsAtTheExactHalfPoint()
    {
        var spread = NoSweatTestData.Market();
        var over = spread.Promo with { MarketKey = "totals", SelectionKey = "over", SelectionDisplayName = "Over", Line = 44.5m };
        var under = spread.Hedge with { MarketKey = "totals", SelectionKey = "under", SelectionDisplayName = "Under", Line = 44.5m };
        var total = new HedgeMarket(spread.Event with { ProviderEventId = "total", Quotes = [over, under] }, over, under);
        var result = Match(NoSweatTestData.Batch(spread, total));
        Assert.Equal(2, result.Count);
        Assert.Equal("home", result[0].Promo.SelectionKey);
        Assert.Equal("away", result[0].Hedge.SelectionKey);
        Assert.Equal("over", result[1].Promo.SelectionKey);
        Assert.Equal("under", result[1].Hedge.SelectionKey);
    }

    [Theory]
    [InlineData("same-selection")]
    [InlineData("same-book")]
    [InlineData("whole-point")]
    [InlineData("quarter-point")]
    [InlineData("mismatched-line")]
    [InlineData("started")]
    [InlineData("stale-promo")]
    [InlineData("stale-hedge")]
    [InlineData("future-source")]
    [InlineData("live")]
    [InlineData("moneyline")]
    [InlineData("unknown-team")]
    public void RejectsNonComplementaryOrIneligibleQuotes(string cause)
    {
        var market = NoSweatTestData.Market();
        var promo = market.Promo;
        var hedge = market.Hedge;
        var game = market.Event;
        switch (cause)
        {
            case "same-selection": hedge = hedge with { SelectionKey = promo.SelectionKey }; break;
            case "same-book": hedge = hedge with { BookmakerKey = "betmgm" }; break;
            case "whole-point": promo = promo with { Line = 3m }; hedge = hedge with { Line = -3m }; break;
            case "quarter-point": promo = promo with { Line = 3.25m }; hedge = hedge with { Line = -3.25m }; break;
            case "mismatched-line": hedge = hedge with { Line = -4.5m }; break;
            case "started": game = game with { CommenceTimeUtc = NoSweatTestData.Now }; break;
            case "stale-promo": promo = promo with { SourceUpdatedAtUtc = NoSweatTestData.Now.AddSeconds(-91) }; break;
            case "stale-hedge": hedge = hedge with { SourceUpdatedAtUtc = NoSweatTestData.Now.AddSeconds(-91) }; break;
            case "future-source": hedge = hedge with { SourceUpdatedAtUtc = NoSweatTestData.Now.AddMinutes(1) }; break;
            case "live": hedge = hedge with { Period = "live" }; break;
            case "moneyline": promo = promo with { MarketKey = "h2h", Line = null }; break;
            case "unknown-team": promo = promo with { SelectionKey = "other-team" }; break;
        }
        var batch = NoSweatTestData.Batch(new HedgeMarket(game with { Quotes = [promo, hedge] }, promo, hedge));
        Assert.Empty(Match(batch));
    }

    [Fact]
    public void NeverCombinesQuotesFromDifferentEvents()
    {
        var market = NoSweatTestData.Market();
        var batch = NoSweatTestData.Batch(market) with
        {
            Events = [market.Event with { Quotes = [market.Promo] },
                market.Event with { ProviderEventId = "event-2", Quotes = [market.Hedge] }]
        };
        Assert.Empty(Match(batch));
    }

    private static IReadOnlyList<HedgeMarket> Match(NormalizedOddsBatch batch) =>
        HedgeMarketMatcher.Match([batch], NoSweatTestData.Options, NoSweatTestData.Now, TimeSpan.FromSeconds(90));
}
