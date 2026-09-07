using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.NoSweat;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.Extensions.Options;

namespace ABOdds;

public static class ApplicationHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args.Where(arg => arg != "--once").ToArray(),
            ContentRootPath = AppContext.BaseDirectory
        });
        if (args.Contains("--once", StringComparer.Ordinal))
        {
            builder.Configuration["Polling:RunOnce"] = "true";
            builder.Configuration["OddsApi:Enabled"] = "true";
        }
        if (builder.Configuration.GetValue<bool>("Polling:RunOnce"))
        {
            // One-shot runs never send real Discord messages.
            builder.Configuration["Discord:Enabled"] = "false";
        }

        builder.Services.AddOptions<OddsApiOptions>()
            .Bind(builder.Configuration.GetSection(OddsApiOptions.SectionName))
            .Validate(OptionsValidation.IsValidOddsApi, OptionsValidation.OddsApiError)
            .ValidateOnStart();
        builder.Services.AddOptions<PollingOptions>()
            .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
            .Validate(OptionsValidation.IsValidPolling, OptionsValidation.PollingError)
            .ValidateOnStart();
        builder.Services.AddOptions<DiscordOptions>()
            .Bind(builder.Configuration.GetSection(DiscordOptions.SectionName))
            .Validate(OptionsValidation.IsValidDiscord, OptionsValidation.DiscordError)
            .ValidateOnStart();

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(serviceProvider => new AdaptivePollingSchedule(
            serviceProvider.GetRequiredService<IOptions<PollingOptions>>().Value));

        builder.Services.AddHttpClient<IOddsProvider, TheOddsApiClient>()
            .RemoveAllLoggers();
        builder.Services.AddHttpClient<IDiscordWebhookClient, DiscordWebhookClient>()
            .RemoveAllLoggers();

        if (builder.Configuration.GetValue<bool>("NoSweat:Enabled"))
        {
            builder.Services.AddOptions<NoSweatOptions>()
                .Bind(builder.Configuration.GetSection(NoSweatOptions.SectionName))
                .Validate(NoSweatOptions.IsValid, "NoSweat requires a promo book, one to nine distinct hedge books, positive cash amounts in cents, a valid stage, and minimum decimal odds of at least one.")
                .ValidateOnStart();
            builder.Services.AddSingleton(services =>
            {
                var options = services.GetRequiredService<IOptions<NoSweatOptions>>().Value;
                return new OddsRequest([options.PromoBook, .. options.HedgeBooks.Select(book => book.Key)],
                    ["spreads", "totals"]);
            });
            builder.Services.AddSingleton<NoSweatWorker>();
            builder.Services.AddHostedService(services => services.GetRequiredService<NoSweatWorker>());
            return builder;
        }

        builder.Services.AddOptions<FairValueOptions>()
            .Bind(builder.Configuration.GetSection(FairValueOptions.SectionName))
            .Validate(OptionsValidation.IsValidFairValue, OptionsValidation.FairValueError)
            .ValidateOnStart();
        builder.Services.AddOptions<EvOptions>()
            .Bind(builder.Configuration.GetSection(EvOptions.SectionName))
            .Validate(OptionsValidation.IsValidEv, OptionsValidation.EvError)
            .Validate<IOptions<FairValueOptions>>((ev, references) =>
                OptionsValidation.AreBookSetsValid(references.Value, ev), OptionsValidation.BookSetsError)
            .ValidateOnStart();
        builder.Services.AddPooledDbContextFactory<BettingDbContext>(options =>
        {
            var connectionString = builder.Configuration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
            }
            BettingDbContext.ConfigurePostgres(options, connectionString);
        });

        builder.Services.AddSingleton<OddsPipeline>();
        builder.Services.AddSingleton(_ => new SemaphoreSlim(1, 1));
        builder.Services.AddSingleton<OddsIngestionRepository>();
        builder.Services.AddSingleton<CalculationRepository>();
        builder.Services.AddSingleton<AlertRepository>();
        builder.Services.AddSingleton(services => new OddsRequest(
            services.GetRequiredService<IOptions<FairValueOptions>>().Value.ReferenceBooks.Select(book => book.Key)
                .Concat(services.GetRequiredService<IOptions<EvOptions>>().Value.TargetBooks.Select(book => book.Key))
                .ToArray(), services.GetRequiredService<IOptions<OddsApiOptions>>().Value.Markets));

        builder.Services.AddHostedService<DatabaseInitializer>();
        builder.Services.AddSingleton<OddsPollingWorker>();
        builder.Services.AddHostedService(services => services.GetRequiredService<OddsPollingWorker>());
        builder.Services.AddHostedService<DiscordAlertWorker>();
        return builder;
    }
}
