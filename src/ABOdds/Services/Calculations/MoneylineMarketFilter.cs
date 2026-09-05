using ABOdds.Domain;

namespace ABOdds.Services.Calculations;

internal static class MoneylineMarketFilter
{
    public static HashSet<(Guid MarketId, string BookmakerKey)> GetUnsupportedMarkets(
        IEnumerable<MarketQuote> quotes) =>
        quotes
            // Inspect every outcome before freshness filtering so a stale draw cannot look like a two-way market.
            .Where(quote => string.Equals(quote.MarketKey, MarketKeys.Moneyline, StringComparison.OrdinalIgnoreCase)
                && OddsKey.NormalizeSelection(quote.SelectionKey) != OddsKey.NormalizeSelection(quote.HomeTeam)
                && OddsKey.NormalizeSelection(quote.SelectionKey) != OddsKey.NormalizeSelection(quote.AwayTeam))
            .Select(quote => (quote.MarketId, quote.BookmakerKey.Trim().ToLowerInvariant()))
            .ToHashSet();
}
