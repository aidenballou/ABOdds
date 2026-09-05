using ABOdds.Configuration;
using ABOdds.Domain;

namespace ABOdds.Services.Calculations;

public static class EvScanner
{
    public static IReadOnlyList<CalculatedEvOpportunity> Scan(
        IEnumerable<MarketQuote> quotes,
        IEnumerable<PersistedFairValue> fairValues,
        DateTimeOffset asOfUtc,
        TimeSpan maximumSourceAge,
        EvOptions options)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(fairValues);
        ArgumentNullException.ThrowIfNull(options);

        if (maximumSourceAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSourceAge),
                "Maximum source age cannot be negative.");
        }

        var targetBooks = options.TargetBooks
            .Where(book => !string.IsNullOrWhiteSpace(book.Key))
            .Select(book => Normalize(book.Key))
            .ToHashSet(StringComparer.Ordinal);

        var quoteList = quotes.ToList();
        var unsupportedMarkets = MoneylineMarketFilter.GetUnsupportedMarkets(quoteList);
        var fairValuesBySelection = fairValues
            .Where(value => value.Sources.All(source =>
                !unsupportedMarkets.Contains((value.MarketId, Normalize(source.BookmakerKey)))))
            .Where(value => value.FairProbability is > 0m and < 1m)
            .Where(value => value.Sources.Count > 0)
            .Where(value => value.Sources.All(
                source => source.SourceUpdatedAtUtc >= asOfUtc - maximumSourceAge))
            .ToDictionary(value => new SelectionLineKey(
                value.MarketId,
                OddsKey.NormalizeSelection(value.SelectionKey),
                value.Line));

        var opportunities = new List<CalculatedEvOpportunity>();

        foreach (var quote in quoteList.Where(quote =>
                     targetBooks.Contains(Normalize(quote.BookmakerKey))
                     && !unsupportedMarkets.Contains((quote.MarketId, Normalize(quote.BookmakerKey)))
                     && IsPregameAndFresh(quote, asOfUtc, maximumSourceAge)
                     && quote.DecimalOdds > 1m))
        {
            var key = new SelectionLineKey(
                quote.MarketId,
                OddsKey.NormalizeSelection(quote.SelectionKey),
                quote.Line);

            if (!fairValuesBySelection.TryGetValue(key, out var fairValue))
            {
                continue;
            }

            var validator = fairValue.Sources.SingleOrDefault(source => source.Role == FairValueSourceRole.Validation);
            if (validator is not null)
            {
                var primary = fairValue.Sources.Single(source => source.Role == FairValueSourceRole.Primary);
                if (Math.Abs(primary.NoVigProbability - validator.NoVigProbability) * quote.DecimalOdds >
                    options.MaximumReferenceEvDifference)
                {
                    continue;
                }
            }

            var expectedValue = fairValue.FairProbability * quote.DecimalOdds - 1m;
            if (expectedValue < options.MinimumExpectedValue)
            {
                continue;
            }

            opportunities.Add(new CalculatedEvOpportunity(
                fairValue.Id,
                quote.SnapshotId,
                quote.EventId,
                quote.MarketId,
                quote.ProviderEventId,
                quote.SportKey,
                quote.HomeTeam,
                quote.AwayTeam,
                quote.CommenceTimeUtc,
                quote.MarketKey,
                quote.BookmakerKey,
                quote.BookmakerTitle,
                quote.SelectionKey,
                quote.SelectionDisplayName,
                quote.Line,
                quote.DecimalOdds,
                fairValue.FairProbability,
                fairValue.FairDecimalOdds,
                expectedValue,
                fairValue.Sources));
        }

        return opportunities
            .OrderByDescending(opportunity => opportunity.ExpectedValue)
            .ThenBy(opportunity => opportunity.ProviderEventId, StringComparer.Ordinal)
            .ThenBy(opportunity => opportunity.MarketKey, StringComparer.Ordinal)
            .ThenBy(opportunity => opportunity.SelectionKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(opportunity => opportunity.BookmakerKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPregameAndFresh(
        MarketQuote quote,
        DateTimeOffset asOfUtc,
        TimeSpan maximumSourceAge) =>
        string.Equals(quote.Period, MarketPeriods.Pregame, StringComparison.OrdinalIgnoreCase)
        && quote.CommenceTimeUtc > asOfUtc
        && quote.SourceUpdatedAtUtc >= asOfUtc - maximumSourceAge;

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private readonly record struct SelectionLineKey(
        Guid MarketId,
        string SelectionKey,
        decimal? Line);
}
