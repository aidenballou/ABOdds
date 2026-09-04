using System.Text.Json.Serialization;

namespace ABOdds.Providers;

public sealed record TheOddsApiEventDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("sport_key")]
    public string SportKey { get; init; } = string.Empty;

    [JsonPropertyName("sport_title")]
    public string SportTitle { get; init; } = string.Empty;

    [JsonPropertyName("commence_time")]
    public DateTimeOffset CommenceTime { get; init; }

    [JsonPropertyName("home_team")]
    public string HomeTeam { get; init; } = string.Empty;

    [JsonPropertyName("away_team")]
    public string AwayTeam { get; init; } = string.Empty;

    [JsonPropertyName("bookmakers")]
    public IReadOnlyList<TheOddsApiBookmakerDto> Bookmakers { get; init; } = [];
}

public sealed record TheOddsApiBookmakerDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("last_update")]
    public DateTimeOffset? LastUpdate { get; init; }

    [JsonPropertyName("markets")]
    public IReadOnlyList<TheOddsApiMarketDto> Markets { get; init; } = [];
}

public sealed record TheOddsApiMarketDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("last_update")]
    public DateTimeOffset? LastUpdate { get; init; }

    [JsonPropertyName("outcomes")]
    public IReadOnlyList<TheOddsApiOutcomeDto> Outcomes { get; init; } = [];
}

public sealed record TheOddsApiOutcomeDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("price")]
    public decimal Price { get; init; }

    [JsonPropertyName("point")]
    public decimal? Point { get; init; }
}
