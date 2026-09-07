namespace ABOdds.Providers;

public sealed record OddsRequest(IReadOnlyList<string> BookmakerKeys, IReadOnlyList<string> MarketKeys);
