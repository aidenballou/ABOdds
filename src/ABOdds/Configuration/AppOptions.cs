namespace ABOdds.Configuration;

public sealed class OddsApiOptions
{
    public const string SectionName = "OddsApi";

    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "https://api.the-odds-api.com/v4/";
    public string ApiKey { get; init; } = string.Empty;
    public IReadOnlyList<string> Sports { get; init; } = [];
    public IReadOnlyList<string> Markets { get; init; } = [];
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class PollingOptions
{
    public const string SectionName = "Polling";

    public TimeSpan NormalInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan NearEventInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan NearEventWindow { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan MaximumSourceAge { get; init; } = TimeSpan.FromSeconds(90);
    public bool RunOnce { get; init; }
    public int? MaximumCreditsPerRun { get; init; }
}

public sealed class FairValueOptions
{
    public const string SectionName = "FairValue";

    public IReadOnlyList<ReferenceBookOptions> ReferenceBooks { get; init; } = [];
}

public sealed class ReferenceBookOptions
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class EvOptions
{
    public const string SectionName = "Ev";

    public decimal MinimumExpectedValue { get; init; } = 0.03m;
    public decimal MaximumReferenceEvDifference { get; init; } = 0.03m;
    public decimal RealertImprovement { get; init; } = 0.01m;
    public IReadOnlyList<TargetBookOptions> TargetBooks { get; init; } = [];
}

public sealed class TargetBookOptions
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public bool Enabled { get; init; }
    public string WebhookUrl { get; init; } = string.Empty;
    public string Username { get; init; } = "ABOdds";
    public TimeSpan OutboxPollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
