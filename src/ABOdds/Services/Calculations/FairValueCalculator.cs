using ABOdds.Configuration;
using ABOdds.Domain;

namespace ABOdds.Services.Calculations;

public static class FairValueCalculator
{
    public const string Version = "pinnacle-devig-betonline-validation-v2";
    public static IReadOnlyList<CalculatedFairValue> Calculate(
        IEnumerable<MarketQuote> quotes,
        DateTimeOffset asOfUtc,
        TimeSpan maximumSourceAge,
        FairValueOptions options)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(options);

        if (maximumSourceAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSourceAge),
                "Maximum source age cannot be negative.");
        }

        var referenceBooks = options.ReferenceBooks
            .Select(book => Normalize(book.Key))
            .Where(key => key is "pinnacle" or "betonlineag")
            .ToHashSet(StringComparer.Ordinal);

        var quoteList = quotes.ToList();
        var unsupportedMarkets = MoneylineMarketFilter.GetUnsupportedMarkets(quoteList);
        var eligibleQuotes = quoteList
            .Where(quote => !unsupportedMarkets.Contains((quote.MarketId, Normalize(quote.BookmakerKey))))
            .Where(quote => IsPregameAndFresh(quote, asOfUtc, maximumSourceAge))
            .Where(quote => quote.DecimalOdds > 1m)
            .Where(quote => referenceBooks.Contains(Normalize(quote.BookmakerKey)))
            .ToList();

        var sourceProbabilities = new List<SourceProbability>();

        foreach (var bookMarket in eligibleQuotes.GroupBy(quote => new BookMarketKey(
                     quote.MarketId,
                     Normalize(quote.MarketKey),
                     Normalize(quote.BookmakerKey))))
        {
            foreach (var outcomeGroup in GroupOutcomes(bookMarket))
            {
                var outcomes = GetCompleteOutcomes(bookMarket.Key.MarketKey, outcomeGroup);
                if (outcomes is null)
                {
                    continue;
                }

                var impliedProbabilityTotal = outcomes.Sum(outcome => 1m / outcome.DecimalOdds);
                var outcomeSetKey = string.Join(
                    "\u001F",
                    outcomes
                        .Select(outcome => OddsKey.NormalizeSelection(outcome.SelectionKey))
                        .Order(StringComparer.Ordinal));

                foreach (var outcome in outcomes)
                {
                    var noVigProbability = (1m / outcome.DecimalOdds) / impliedProbabilityTotal;
                    sourceProbabilities.Add(new SourceProbability(
                        outcome with { SourceUpdatedAtUtc = outcomes.Min(value => value.SourceUpdatedAtUtc) },
                        noVigProbability,
                        outcomeSetKey));
                }
            }
        }

        var fairValues = new List<CalculatedFairValue>();

        foreach (var selectionGroup in sourceProbabilities.GroupBy(source => new SelectionLineKey(
                     source.Quote.MarketId,
                     OddsKey.NormalizeSelection(source.Quote.SelectionKey),
                     source.Quote.Line)))
        {
            var contributors = selectionGroup
                .GroupBy(source => Normalize(source.Quote.BookmakerKey))
                .Select(group => group
                    .OrderByDescending(source => source.Quote.SourceUpdatedAtUtc)
                    .First())
                .ToList();

            var primary = contributors.FirstOrDefault(source => Normalize(source.Quote.BookmakerKey) == "pinnacle");
            var fallback = contributors.FirstOrDefault(source => Normalize(source.Quote.BookmakerKey) == "betonlineag");
            var selected = primary ?? fallback!;
            var validator = primary is not null && fallback?.OutcomeSetKey == primary.OutcomeSetKey
                ? fallback
                : null;
            var fairProbability = selected.NoVigProbability;
            var sources = new List<FairValueSource>
            {
                ToSource(selected, primary is not null ? FairValueSourceRole.Primary : FairValueSourceRole.Fallback)
            };
            if (validator is not null) sources.Add(ToSource(validator, FairValueSourceRole.Validation));

            var first = selected.Quote;
            fairValues.Add(new CalculatedFairValue(
                selectionGroup.Key.MarketId,
                first.SelectionKey,
                first.SelectionDisplayName,
                selectionGroup.Key.Line,
                fairProbability,
                1m / fairProbability,
                sources));
        }

        return fairValues
            .OrderBy(value => value.MarketId)
            .ThenBy(value => value.SelectionKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Line)
            .ToList();
    }

    private static FairValueSource ToSource(SourceProbability source, FairValueSourceRole role) =>
        new(source.Quote.BookmakerKey, source.Quote.BookmakerTitle, source.Quote.DecimalOdds,
            source.NoVigProbability, role == FairValueSourceRole.Validation ? 0m : 1m,
            source.Quote.SourceUpdatedAtUtc) { Role = role };

    private static IEnumerable<IEnumerable<MarketQuote>> GroupOutcomes(
        IEnumerable<MarketQuote> bookMarket)
    {
        var quotes = bookMarket.ToList();
        var marketKey = Normalize(quotes[0].MarketKey);

        return marketKey switch
        {
            MarketKeys.Moneyline => quotes
                .Where(quote => quote.Line is null)
                .GroupBy(_ => 0m),
            MarketKeys.Spread => quotes
                .Where(quote => quote.Line is not null)
                .GroupBy(quote => Math.Abs(quote.Line!.Value)),
            MarketKeys.Total => quotes
                .Where(quote => quote.Line is not null)
                .GroupBy(quote => quote.Line!.Value),
            _ => []
        };
    }

    private static List<MarketQuote>? GetCompleteOutcomes(
        string marketKey,
        IEnumerable<MarketQuote> outcomeGroup)
    {
        var outcomes = outcomeGroup
            .GroupBy(quote => OddsKey.NormalizeSelection(quote.SelectionKey))
            .Select(group => group
                .OrderByDescending(quote => quote.SourceUpdatedAtUtc)
                .First())
            .ToList();

        var first = outcomes[0];
        var expectedTeams = new HashSet<string>(StringComparer.Ordinal)
        {
            OddsKey.NormalizeSelection(first.HomeTeam),
            OddsKey.NormalizeSelection(first.AwayTeam)
        };
        var actualSelections = outcomes
            .Select(outcome => OddsKey.NormalizeSelection(outcome.SelectionKey))
            .ToHashSet(StringComparer.Ordinal);

        if (marketKey == MarketKeys.Moneyline)
        {
            return outcomes.Count == 2 && expectedTeams.SetEquals(actualSelections)
                ? outcomes
                : null;
        }

        if (outcomes.Count != 2)
        {
            return null;
        }

        if (marketKey == MarketKeys.Spread)
        {
            var hasOpposingLines = outcomes[0].Line == -outcomes[1].Line;
            return expectedTeams.SetEquals(actualSelections) && hasOpposingLines
                ? outcomes
                : null;
        }

        if (marketKey == MarketKeys.Total)
        {
            var expectedTotals = new HashSet<string>(StringComparer.Ordinal) { "over", "under" };
            var hasMatchingLines = outcomes[0].Line == outcomes[1].Line;
            return expectedTotals.SetEquals(actualSelections) && hasMatchingLines
                ? outcomes
                : null;
        }

        return null;
    }

    private static bool IsPregameAndFresh(
        MarketQuote quote,
        DateTimeOffset asOfUtc,
        TimeSpan maximumSourceAge) =>
        string.Equals(quote.Period, MarketPeriods.Pregame, StringComparison.OrdinalIgnoreCase)
        && quote.CommenceTimeUtc > asOfUtc
        && quote.SourceUpdatedAtUtc >= asOfUtc - maximumSourceAge;

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private readonly record struct BookMarketKey(
        Guid MarketId,
        string MarketKey,
        string BookmakerKey);

    private readonly record struct SelectionLineKey(
        Guid MarketId,
        string SelectionKey,
        decimal? Line);

    private sealed record SourceProbability(
        MarketQuote Quote,
        decimal NoVigProbability,
        string OutcomeSetKey);
}
