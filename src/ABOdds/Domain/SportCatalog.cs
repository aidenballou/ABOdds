namespace ABOdds.Domain;

public sealed record SportDefinition(string DisplayName, string SpreadName);

public static class SportCatalog
{
    private static readonly IReadOnlyDictionary<string, SportDefinition> Sports =
        new Dictionary<string, SportDefinition>(StringComparer.Ordinal)
        {
            ["americanfootball_nfl"] = new("NFL", "Spread"),
            ["americanfootball_ncaaf"] = new("NCAAF", "Spread"),
            ["baseball_mlb"] = new("MLB", "Run line")
        };

    public static IEnumerable<string> Keys => Sports.Keys;

    public static SportDefinition? Find(string key) => Sports.GetValueOrDefault(key);
}
