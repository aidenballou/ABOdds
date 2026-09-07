using ABOdds.Domain;
using ABOdds.NoSweat;

namespace ABOdds.Tests.NoSweat;

internal static class NoSweatTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    internal static NoSweatOptions Options => new()
    {
        Enabled = true,
        HedgeBooks = [new() { Key = "fanduel" }, new() { Key = "draftkings" }]
    };

    internal static HedgeMarket Market(decimal promo = 2m, decimal hedge = 2m, string id = "event-1", string hedgeBook = "fanduel")
    {
        var first = new NormalizedQuote("spreads", "pregame", "betmgm", "BetMGM", "home", "Home", promo, 3.5m, Now);
        var second = new NormalizedQuote("spreads", "pregame", hedgeBook, hedgeBook, "away", "Away", hedge, -3.5m, Now);
        var game = new NormalizedEvent(id, "americanfootball_nfl", "Home", "Away", Now.AddHours(2), [first, second]);
        return new(game, first, second);
    }

    internal static NormalizedOddsBatch Batch(params HedgeMarket[] markets) =>
        new("americanfootball_nfl", Now, markets.Select(market => market.Event).ToArray(), new(1000, 2, 2));
}
