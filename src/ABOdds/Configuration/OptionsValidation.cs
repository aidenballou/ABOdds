using ABOdds.Domain;

namespace ABOdds.Configuration;

internal static class OptionsValidation
{
    public static readonly string OddsApiError =
        "OddsApi requires an HTTPS base URL, an API key when enabled, unique supported markets, and a positive timeout. " +
        "OddsApi:EnabledSports must be a nonempty comma-separated list of unique supported keys: " + string.Join(", ", SportCatalog.Keys);
    public const string PollingError =
        "Polling intervals, near-event window, and maximum source age must be positive; the near interval cannot exceed the normal interval.";
    public const string FairValueError =
        "FairValue requires exactly Pinnacle and BetOnline as reference books.";
    public const string EvError =
        "Ev requires positive thresholds and unique nonempty target keys.";
    public const string BookSetsError =
        "Reference and target books must not overlap, and V1 allows at most ten unique books to stay in one quota group.";
    public const string DiscordError =
        "Discord requires positive polling/retry intervals and an HTTPS webhook URL when enabled.";

    public static bool IsValidOddsApi(OddsApiOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (!options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey)) &&
        options.Sports.Count > 0 &&
        options.Sports.Distinct(StringComparer.Ordinal).Count() == options.Sports.Count &&
        options.Sports.All(value => SportCatalog.Find(value) is not null) &&
        options.Markets.Count > 0 &&
        options.Markets.Distinct(StringComparer.Ordinal).Count() == options.Markets.Count &&
        options.Markets.All(value =>
            value is "h2h" or "spreads" or "totals") &&
        options.RequestTimeout > TimeSpan.Zero;

    public static bool IsValidPolling(PollingOptions options) =>
        options.NormalInterval > TimeSpan.Zero &&
        options.NearEventInterval > TimeSpan.Zero &&
        options.NearEventInterval <= options.NormalInterval &&
        options.NearEventWindow > TimeSpan.Zero &&
        options.MaximumSourceAge > TimeSpan.Zero &&
        (options.MaximumCreditsPerRun is null or > 0);

    public static bool IsValidFairValue(FairValueOptions options)
    {
        return options.ReferenceBooks.Count == 2 &&
               options.ReferenceBooks.All(value => !string.IsNullOrWhiteSpace(value.Key)) &&
               options.ReferenceBooks.Select(value => value.Key.Trim())
                   .ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(["pinnacle", "betonlineag"]);
    }

    public static bool IsValidEv(EvOptions options)
    {
        return options.MinimumExpectedValue > 0m &&
               options.MaximumReferenceEvDifference > 0m &&
               options.RealertImprovement > 0m &&
               options.TargetBooks.Count > 0 &&
               options.TargetBooks.All(value => !string.IsNullOrWhiteSpace(value.Key)) &&
               options.TargetBooks.Select(value => value.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == options.TargetBooks.Count;
    }

    public static bool AreBookSetsValid(FairValueOptions references, EvOptions ev)
    {
        if (!IsValidFairValue(references) || !IsValidEv(ev)) return false;
        var referenceKeys = references.ReferenceBooks.Select(book => book.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return references.ReferenceBooks.Count + ev.TargetBooks.Count <= 10 &&
            ev.TargetBooks.All(book => !referenceKeys.Contains(book.Key.Trim()));
    }

    public static bool IsValidDiscord(DiscordOptions options) =>
        options.OutboxPollInterval > TimeSpan.Zero &&
        options.MaximumRetryDelay > TimeSpan.Zero &&
        options.RequestTimeout > TimeSpan.Zero &&
        (!options.Enabled ||
         (Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps));
}
