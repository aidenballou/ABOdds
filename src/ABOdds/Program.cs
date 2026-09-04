using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<OddsApiOptions>()
    .Bind(builder.Configuration.GetSection(OddsApiOptions.SectionName))
    .Validate(OptionsValidation.IsValidOddsApi, OptionsValidation.OddsApiError)
    .ValidateOnStart();
builder.Services.AddOptions<PollingOptions>()
    .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
    .Validate(OptionsValidation.IsValidPolling, OptionsValidation.PollingError)
    .ValidateOnStart();
builder.Services.AddOptions<FairValueOptions>()
    .Bind(builder.Configuration.GetSection(FairValueOptions.SectionName))
    .Validate(OptionsValidation.IsValidFairValue, OptionsValidation.FairValueError)
    .ValidateOnStart();
builder.Services.AddOptions<EvOptions>()
    .Bind(builder.Configuration.GetSection(EvOptions.SectionName))
    .Validate(OptionsValidation.IsValidEv, OptionsValidation.EvError)
    .ValidateOnStart();
builder.Services.AddOptions<DiscordOptions>()
    .Bind(builder.Configuration.GetSection(DiscordOptions.SectionName))
    .Validate(OptionsValidation.IsValidDiscord, OptionsValidation.DiscordError)
    .ValidateOnStart();
builder.Services.AddOptions<PipelineOptions>()
    .Bind(builder.Configuration.GetSection(PipelineOptions.SectionName))
    .Validate(value => value.ChannelCapacity > 0, "Pipeline:ChannelCapacity must be positive.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
}

builder.Services.AddPooledDbContextFactory<BettingDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null)));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IOddsNormalizer, OddsNormalizer>();
builder.Services.AddSingleton<PipelineChannels>();
builder.Services.AddSingleton(_ => new SemaphoreSlim(1, 1));
builder.Services.AddSingleton<OddsIngestionRepository>();
builder.Services.AddSingleton<CalculationRepository>();
builder.Services.AddSingleton<AlertRepository>();
builder.Services.AddSingleton<AlertDecisionService>();
builder.Services.AddSingleton(serviceProvider => new AdaptivePollingSchedule(
    serviceProvider.GetRequiredService<IOptions<PollingOptions>>().Value));

builder.Services.AddHttpClient<IOddsProvider, TheOddsApiClient>()
    .RemoveAllLoggers();
builder.Services.AddHttpClient<IDiscordWebhookClient, DiscordWebhookClient>()
    .RemoveAllLoggers();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<OddsPollingWorker>();
builder.Services.AddHostedService<FairValueWorker>();
builder.Services.AddHostedService<EvDetectionWorker>();
builder.Services.AddHostedService<AlertRuleWorker>();
builder.Services.AddHostedService<DiscordAlertWorker>();

await builder.Build().RunAsync();

internal static class OptionsValidation
{
    public const string OddsApiError =
        "OddsApi requires a valid base URL, an API key when enabled, supported sports/markets, and a positive timeout.";
    public const string PollingError =
        "Polling intervals, near-event window, and maximum source age must be positive; the near interval cannot exceed the normal interval.";
    public const string FairValueError =
        "FairValue requires unique reference keys with positive weights and a reachable minimum reference-book count.";
    public const string EvError =
        "Ev requires a non-negative threshold and re-alert delta plus at least one unique target book.";
    public const string DiscordError =
        "Discord requires positive polling/retry intervals and an HTTPS webhook URL when enabled.";

    public static bool IsValidOddsApi(OddsApiOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (!options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey)) &&
        options.Sports.Count > 0 &&
        options.Sports.All(value =>
            value is "americanfootball_nfl" or "americanfootball_ncaaf") &&
        options.Markets.Count > 0 &&
        options.Markets.All(value =>
            value is "h2h" or "spreads" or "totals") &&
        options.RequestTimeout > TimeSpan.Zero;

    public static bool IsValidPolling(PollingOptions options) =>
        options.NormalInterval > TimeSpan.Zero &&
        options.NearEventInterval > TimeSpan.Zero &&
        options.NearEventInterval <= options.NormalInterval &&
        options.NearEventWindow > TimeSpan.Zero &&
        options.MaximumSourceAge > TimeSpan.Zero;

    public static bool IsValidFairValue(FairValueOptions options)
    {
        var validBooks = options.ReferenceBooks
            .Where(value => !string.IsNullOrWhiteSpace(value.Key) && value.Weight > 0m)
            .ToArray();
        return options.MinimumReferenceBooks > 0 &&
               options.MinimumReferenceBooks <= validBooks.Length &&
               validBooks.Select(value => value.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
               validBooks.Length;
    }

    public static bool IsValidEv(EvOptions options)
    {
        var validKeys = options.TargetBooks
            .Select(value => value.Key)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return options.MinimumExpectedValue >= 0m &&
               options.RealertImprovement >= 0m &&
               validKeys.Length > 0 &&
               validKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == validKeys.Length;
    }

    public static bool IsValidDiscord(DiscordOptions options) =>
        options.OutboxPollInterval > TimeSpan.Zero &&
        options.MaximumRetryDelay > TimeSpan.Zero &&
        (!options.Enabled ||
         (Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps));
}
