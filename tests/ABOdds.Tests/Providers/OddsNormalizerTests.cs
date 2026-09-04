using ABOdds.Domain;
using ABOdds.Providers;

namespace ABOdds.Tests.Providers;

public sealed class OddsNormalizerTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 9, 3, 22, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Normalize_FiltersCommencedInvalidAndUnsupportedQuotes()
    {
        var normalizer = new OddsNormalizer();
        var sourceEvents = new[]
        {
            CreateFutureEvent(),
            CreateEvent("already-started", ObservedAt),
            CreateEvent("past", ObservedAt.AddMinutes(-1))
        };

        var result = normalizer.Normalize(
            "americanfootball_nfl",
            ObservedAt,
            sourceEvents,
            new ApiQuotaSnapshot(90, 10, 3));

        var normalizedEvent = Assert.Single(result.Events);
        Assert.Equal("future", normalizedEvent.ProviderEventId);
        Assert.Equal("americanfootball_nfl", normalizedEvent.SportKey);
        Assert.Equal(ObservedAt, result.ObservedAtUtc);
        Assert.Equal(new ApiQuotaSnapshot(90, 10, 3), result.Quota);
        Assert.Equal(6, normalizedEvent.Quotes.Count);
        Assert.DoesNotContain(normalizedEvent.Quotes, quote => quote.DecimalOdds <= 1m);
        Assert.DoesNotContain(
            normalizedEvent.Quotes,
            quote => quote.MarketKey == "player_pass_yds");
    }

    [Fact]
    public void Normalize_UsesNormalizedSelectionAndMostSpecificSourceTimestamp()
    {
        var result = new OddsNormalizer().Normalize(
            "americanfootball_nfl",
            ObservedAt,
            [CreateFutureEvent()],
            new ApiQuotaSnapshot(null, null, null));

        var normalizedEvent = Assert.Single(result.Events);
        var moneyline = Assert.Single(
            normalizedEvent.Quotes,
            quote => quote.MarketKey == MarketKeys.Moneyline &&
                quote.SelectionKey == "indianapolis colts");
        Assert.Equal("Indianapolis   Colts", moneyline.SelectionDisplayName);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 22, 14, 30, TimeSpan.Zero), moneyline.SourceUpdatedAtUtc);
        Assert.Null(moneyline.Line);
        Assert.Equal(MarketPeriods.Pregame, moneyline.Period);

        var spread = Assert.Single(
            normalizedEvent.Quotes,
            quote => quote.MarketKey == MarketKeys.Spread && quote.Line == 3.5m);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 22, 14, 0, TimeSpan.Zero), spread.SourceUpdatedAtUtc);

        var total = Assert.Single(
            normalizedEvent.Quotes,
            quote => quote.MarketKey == MarketKeys.Total && quote.SelectionKey == "over");
        Assert.Equal("Over", total.SelectionDisplayName);
    }

    private static TheOddsApiEventDto CreateFutureEvent() => new()
    {
        Id = " future ",
        SportKey = "americanfootball_nfl",
        HomeTeam = "Houston Texans",
        AwayTeam = "Indianapolis Colts",
        CommenceTime = ObservedAt.AddHours(1),
        Bookmakers =
        [
            new TheOddsApiBookmakerDto
            {
                Key = " FanDuel ",
                Title = " FanDuel ",
                LastUpdate = new DateTimeOffset(2026, 9, 3, 22, 14, 0, TimeSpan.Zero),
                Markets =
                [
                    new TheOddsApiMarketDto
                    {
                        Key = MarketKeys.Moneyline,
                        LastUpdate = new DateTimeOffset(2026, 9, 3, 22, 14, 30, TimeSpan.Zero),
                        Outcomes =
                        [
                            new TheOddsApiOutcomeDto { Name = " Indianapolis   Colts ", Price = 2.05m },
                            new TheOddsApiOutcomeDto { Name = "Houston Texans", Price = 1.80m },
                            new TheOddsApiOutcomeDto { Name = "Bad price", Price = 1m }
                        ]
                    },
                    new TheOddsApiMarketDto
                    {
                        Key = MarketKeys.Spread,
                        Outcomes =
                        [
                            new TheOddsApiOutcomeDto { Name = "Indianapolis Colts", Price = 1.91m, Point = 3.5m },
                            new TheOddsApiOutcomeDto { Name = "Houston Texans", Price = 1.91m, Point = -3.5m },
                            new TheOddsApiOutcomeDto { Name = "No line", Price = 1.91m }
                        ]
                    },
                    new TheOddsApiMarketDto
                    {
                        Key = MarketKeys.Total,
                        LastUpdate = new DateTimeOffset(2026, 9, 3, 22, 14, 45, TimeSpan.Zero),
                        Outcomes =
                        [
                            new TheOddsApiOutcomeDto { Name = " Over ", Price = 1.91m, Point = 44.5m },
                            new TheOddsApiOutcomeDto { Name = "Under", Price = 1.91m, Point = 44.5m }
                        ]
                    },
                    new TheOddsApiMarketDto
                    {
                        Key = "player_pass_yds",
                        LastUpdate = ObservedAt,
                        Outcomes =
                        [
                            new TheOddsApiOutcomeDto { Name = "Over", Price = 1.91m, Point = 250.5m }
                        ]
                    }
                ]
            }
        ]
    };

    private static TheOddsApiEventDto CreateEvent(string id, DateTimeOffset commenceTime) => new()
    {
        Id = id,
        SportKey = "americanfootball_nfl",
        HomeTeam = "Home",
        AwayTeam = "Away",
        CommenceTime = commenceTime
    };
}
