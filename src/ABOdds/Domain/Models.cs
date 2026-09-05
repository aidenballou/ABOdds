using System.Globalization;

namespace ABOdds.Domain;

public sealed record NormalizedOddsBatch(
    string SportKey,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<NormalizedEvent> Events,
    ApiQuotaSnapshot Quota)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record NormalizedEvent(
    string ProviderEventId,
    string SportKey,
    string HomeTeam,
    string AwayTeam,
    DateTimeOffset CommenceTimeUtc,
    IReadOnlyList<NormalizedQuote> Quotes);

public sealed record ObservedEventMetadata(
    string ProviderEventId,
    string SportKey,
    string HomeTeam,
    string AwayTeam,
    DateTimeOffset CommenceTimeUtc);

public sealed record NormalizedQuote(
    string MarketKey,
    string Period,
    string BookmakerKey,
    string BookmakerTitle,
    string SelectionKey,
    string SelectionDisplayName,
    decimal DecimalOdds,
    decimal? Line,
    DateTimeOffset SourceUpdatedAtUtc);

public sealed record ApiQuotaSnapshot(int? Remaining, int? Used, int? LastRequestCost);

public sealed record MarketQuote(
    Guid SnapshotId,
    Guid EventId,
    Guid MarketId,
    string ProviderEventId,
    string SportKey,
    string HomeTeam,
    string AwayTeam,
    DateTimeOffset CommenceTimeUtc,
    string MarketKey,
    string Period,
    string BookmakerKey,
    string BookmakerTitle,
    string SelectionKey,
    string SelectionDisplayName,
    decimal DecimalOdds,
    decimal? Line,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset SourceUpdatedAtUtc)
{
    public string LineKey => OddsKey.FormatLine(Line);
}

public sealed record FairValueSource(
    string BookmakerKey,
    string BookmakerTitle,
    decimal OfferedDecimalOdds,
    decimal NoVigProbability,
    decimal ConfiguredWeight,
    DateTimeOffset SourceUpdatedAtUtc);

public sealed record CalculatedFairValue(
    Guid MarketId,
    string SelectionKey,
    string SelectionDisplayName,
    decimal? Line,
    decimal FairProbability,
    decimal FairDecimalOdds,
    IReadOnlyList<FairValueSource> Sources)
{
    public string LineKey => OddsKey.FormatLine(Line);
}

public sealed record PersistedFairValue(
    Guid Id,
    Guid MarketId,
    string SelectionKey,
    string SelectionDisplayName,
    decimal? Line,
    decimal FairProbability,
    decimal FairDecimalOdds,
    IReadOnlyList<FairValueSource> Sources)
{
    public string LineKey => OddsKey.FormatLine(Line);
}

public sealed record CalculatedEvOpportunity(
    Guid FairValueId,
    Guid OddsSnapshotId,
    Guid EventId,
    Guid MarketId,
    string ProviderEventId,
    string SportKey,
    string HomeTeam,
    string AwayTeam,
    DateTimeOffset CommenceTimeUtc,
    string MarketKey,
    string BookmakerKey,
    string BookmakerTitle,
    string SelectionKey,
    string SelectionDisplayName,
    decimal? Line,
    decimal DecimalOdds,
    decimal FairProbability,
    decimal FairDecimalOdds,
    decimal ExpectedValue,
    IReadOnlyList<FairValueSource> Sources)
{
    public string LineKey => OddsKey.FormatLine(Line);
}

public readonly record struct BetKey(
    Guid EventId,
    Guid MarketId,
    string SelectionKey,
    string LineKey,
    string BookmakerKey);

public static class OddsKey
{
    public static string NormalizeSelection(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    public static string FormatLine(decimal? line) =>
        line?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "none";
}
