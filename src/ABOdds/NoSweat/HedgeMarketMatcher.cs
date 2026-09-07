using ABOdds.Domain;

namespace ABOdds.NoSweat;

public static class HedgeMarketMatcher
{
    public static IReadOnlyList<HedgeMarket> Match(
        IEnumerable<NormalizedOddsBatch> batches, NoSweatOptions options,
        DateTimeOffset now, TimeSpan maximumSourceAge)
    {
        var books = options.HedgeBooks.Select(book => book.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<HedgeMarket>();
        foreach (var game in batches.SelectMany(batch => batch.Events)
                     .GroupBy(game => (game.SportKey, game.ProviderEventId)).Select(group => group.Last())
                     .Where(game => game.CommenceTimeUtc > now))
        {
            var home = OddsKey.NormalizeSelection(game.HomeTeam);
            var away = OddsKey.NormalizeSelection(game.AwayTeam);
            if (home == away) continue;
            var quotes = game.Quotes.Where(quote =>
                    quote.Period == MarketPeriods.Pregame && quote.DecimalOdds > 1m &&
                    quote.SourceUpdatedAtUtc <= now && quote.SourceUpdatedAtUtc >= now - maximumSourceAge &&
                    quote.Line is { } line && Math.Abs(line % 1m) == 0.5m &&
                    (quote.MarketKey == MarketKeys.Spread && (quote.SelectionKey == home || quote.SelectionKey == away) ||
                     quote.MarketKey == MarketKeys.Total && quote.SelectionKey is "over" or "under"))
                .GroupBy(quote => (quote.BookmakerKey, quote.MarketKey, quote.SelectionKey, quote.Line))
                .Select(group => group.MaxBy(quote => quote.SourceUpdatedAtUtc)!).ToArray();
            foreach (var promo in quotes.Where(quote => string.Equals(quote.BookmakerKey,
                         options.PromoBook.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var hedge in quotes.Where(quote => books.Contains(quote.BookmakerKey) &&
                             quote.MarketKey == promo.MarketKey && quote.SelectionKey != promo.SelectionKey &&
                             (promo.MarketKey == MarketKeys.Spread ? quote.Line == -promo.Line : quote.Line == promo.Line)))
                {
                    matches.Add(new(game, promo, hedge));
                }
            }
        }
        return matches;
    }
}
