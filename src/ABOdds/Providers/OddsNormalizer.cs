using ABOdds.Domain;

namespace ABOdds.Providers;

public interface IOddsNormalizer
{
    NormalizedOddsBatch Normalize(
        string sportKey,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<TheOddsApiEventDto> sourceEvents,
        ApiQuotaSnapshot quota);
}

public sealed class OddsNormalizer : IOddsNormalizer
{
    public NormalizedOddsBatch Normalize(
        string sportKey,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<TheOddsApiEventDto> sourceEvents,
        ApiQuotaSnapshot quota)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sportKey);
        ArgumentNullException.ThrowIfNull(sourceEvents);

        var observedAt = observedAtUtc.ToUniversalTime();
        var events = new List<NormalizedEvent>(sourceEvents.Count);

        foreach (var sourceEvent in sourceEvents)
        {
            if (sourceEvent.CommenceTime <= observedAt ||
                string.IsNullOrWhiteSpace(sourceEvent.Id) ||
                string.IsNullOrWhiteSpace(sourceEvent.HomeTeam) ||
                string.IsNullOrWhiteSpace(sourceEvent.AwayTeam))
            {
                continue;
            }

            var quotes = NormalizeQuotes(sourceEvent.Bookmakers);
            events.Add(new NormalizedEvent(
                sourceEvent.Id.Trim(),
                sportKey,
                sourceEvent.HomeTeam.Trim(),
                sourceEvent.AwayTeam.Trim(),
                sourceEvent.CommenceTime.ToUniversalTime(),
                quotes));
        }

        return new NormalizedOddsBatch(sportKey, observedAt, events, quota);
    }

    private static List<NormalizedQuote> NormalizeQuotes(
        IReadOnlyList<TheOddsApiBookmakerDto> bookmakers)
    {
        var quotes = new List<NormalizedQuote>();

        foreach (var bookmaker in bookmakers)
        {
            if (string.IsNullOrWhiteSpace(bookmaker.Key))
            {
                continue;
            }

            var bookmakerKey = bookmaker.Key.Trim().ToLowerInvariant();
            var bookmakerTitle = string.IsNullOrWhiteSpace(bookmaker.Title)
                ? bookmakerKey
                : bookmaker.Title.Trim();

            foreach (var market in bookmaker.Markets)
            {
                if (!IsSupportedMarket(market.Key))
                {
                    continue;
                }

                var sourceUpdatedAt = market.LastUpdate ?? bookmaker.LastUpdate;
                if (sourceUpdatedAt is null)
                {
                    continue;
                }

                foreach (var outcome in market.Outcomes)
                {
                    if (outcome.Price <= 1m || string.IsNullOrWhiteSpace(outcome.Name))
                    {
                        continue;
                    }

                    var line = market.Key == MarketKeys.Moneyline ? null : outcome.Point;
                    if (market.Key != MarketKeys.Moneyline && line is null)
                    {
                        continue;
                    }

                    var displayName = outcome.Name.Trim();
                    quotes.Add(new NormalizedQuote(
                        market.Key,
                        MarketPeriods.Pregame,
                        bookmakerKey,
                        bookmakerTitle,
                        OddsKey.NormalizeSelection(displayName),
                        displayName,
                        outcome.Price,
                        line,
                        sourceUpdatedAt.Value.ToUniversalTime()));
                }
            }
        }

        return quotes;
    }

    private static bool IsSupportedMarket(string marketKey) =>
        marketKey is MarketKeys.Moneyline or MarketKeys.Spread or MarketKeys.Total;
}
