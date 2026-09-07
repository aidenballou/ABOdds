using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Providers;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Persistence;

public sealed class ApplicationHostTests : PostgresTest
{
    [PostgresFact]
    public async Task MlbOnly_RequestsAndPersistsAllThreeMarketsForBothDoubleheaderGames()
    {
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: true);
        builder.Configuration["OddsApi:EnabledSports"] = "baseball_mlb";
        using var host = BuildTestHost(builder);
        var worker = host.Services.GetRequiredService<OddsPollingWorker>();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, worker.ExitCode);
        var request = Assert.Single(odds.Requests);
        Assert.Equal("/v4/sports/baseball_mlb/odds", request.AbsolutePath);
        Assert.Contains("markets=h2h,spreads,totals", Uri.UnescapeDataString(request.Query), StringComparison.Ordinal);
        Assert.Empty(discord.Messages);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.NotNull((await db.PollBatches.SingleAsync()).AlertRulesCompletedAtUtc);
        Assert.Equal(2, await db.Events.CountAsync(game => game.SportKey == "baseball_mlb"));
        Assert.Equal(36, await db.OddsSnapshots.CountAsync());
        Assert.Equal(12, await db.FairValues.CountAsync());
        Assert.Equal(6, await db.EvOpportunities.CountAsync());
        var alerts = await db.Alerts.ToListAsync();
        Assert.Equal(6, alerts.Count);
        Assert.All(alerts, alert =>
        {
            Assert.Equal(AlertDeliveryStatus.Pending, alert.DeliveryStatus);
            Assert.Contains("Arizona Diamondbacks @ Miami Marlins", alert.Message, StringComparison.Ordinal);
            Assert.Contains("MLB |", alert.Message, StringComparison.Ordinal);
            Assert.Contains("+7.7% EV", alert.Message, StringComparison.Ordinal);
        });
        Assert.Equal(2, alerts.Count(alert => alert.Message.Contains("MLB | Moneyline", StringComparison.Ordinal)));
        Assert.Equal(2, alerts.Count(alert => alert.Message.Contains("MLB | Run line", StringComparison.Ordinal)));
        Assert.Equal(2, alerts.Count(alert => alert.Message.Contains("MLB | Total", StringComparison.Ordinal)));
        Assert.Equal(2, await db.OddsSnapshots.CountAsync(quote => quote.BookmakerKey == "fanduel" &&
            quote.SelectionKey == "arizona diamondbacks" && quote.Line == -1.5m));
        Assert.Equal(2, await db.OddsSnapshots.CountAsync(quote => quote.BookmakerKey == "fanduel" &&
            quote.SelectionKey == "over" && quote.Line == 7.5m));
    }

    [PostgresFact]
    public async Task SelectedSports_RequestsOnlyNflAndMlbInConfiguredOrder()
    {
        var odds = new OddsHandler();
        var builder = await BuilderAsync(odds, new DiscordHandler(), once: true);
        builder.Configuration["OddsApi:EnabledSports"] = "americanfootball_nfl,baseball_mlb";
        using var host = BuildTestHost(builder);
        var worker = host.Services.GetRequiredService<OddsPollingWorker>();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, worker.ExitCode);
        Assert.Equal(["/v4/sports/americanfootball_nfl/odds", "/v4/sports/baseball_mlb/odds"],
            odds.Requests.Select(uri => uri.AbsolutePath));
    }

    [PostgresFact]
    public async Task InvalidSport_StopsStartupBeforeAnyHttpRequest()
    {
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: true);
        builder.Configuration["OddsApi:EnabledSports"] = "baseball_mlb,typo";
        using var host = BuildTestHost(builder);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Empty(odds.Requests);
        Assert.Empty(discord.Messages);
    }

    [PostgresFact]
    public async Task RealHost_RunOnce_UsesNineBooksAndPersistsWithoutSendingDiscord()
    {
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: true);
        using var host = BuildTestHost(builder);
        var worker = host.Services.GetRequiredService<OddsPollingWorker>();
        Assert.False(host.Services.GetRequiredService<IOptions<DiscordOptions>>().Value.Enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.RunAsync(timeout.Token);
        Assert.Equal(0, worker.ExitCode);
        Assert.Equal(2, odds.Requests.Count);
        Assert.Empty(discord.Messages);
        foreach (var uri in odds.Requests)
        {
            var query = uri.Query.TrimStart('?').Split('&').Select(value => value.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            Assert.Equal(9, query["bookmakers"].Split(',').Length);
            Assert.DoesNotContain("lowvig", query["bookmakers"], StringComparison.Ordinal);
            Assert.DoesNotContain("betrivers", query["bookmakers"], StringComparison.Ordinal);
            Assert.Equal("2026-09-04T12:00:00Z", query["commenceTimeFrom"]);
        }
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.PollBatches.CountAsync(value => value.AlertRulesCompletedAtUtc != null));
        Assert.Equal(10, await db.OddsSnapshots.CountAsync());
        Assert.Equal(2, await db.Alerts.CountAsync(value => value.DeliveryStatus == AlertDeliveryStatus.Pending));
    }

    [PostgresFact]
    public async Task RealHost_PollsCalculatesAndDeliversThroughBothHttpAdapters()
    {
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: false);
        using var host = BuildTestHost(builder);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = host.RunAsync(timeout.Token);
        try
        {
            await WaitForSentAsync(2, timeout.Token);
            Assert.Equal(2, discord.Messages.Count);
            Assert.All(discord.Messages, message =>
            {
                Assert.Contains("Indianapolis Colts @ Houston Texans", message, StringComparison.Ordinal);
                Assert.Contains("**Indianapolis Colts +3.5** · **+100**", message, StringComparison.Ordinal);
                Assert.Contains("FanDuel · **+7.7% EV**", message, StringComparison.Ordinal);
                Assert.Contains("BetOnline confirmed", message, StringComparison.Ordinal);
            });
            await using var db = await Factory.CreateDbContextAsync();
            var alerts = await db.Alerts.AsNoTracking().ToListAsync();
            Assert.Equal(2, alerts.Count);
            Assert.All(alerts, alert => Assert.Equal(AlertReason.New, alert.Reason));
            Assert.Equal(2, odds.Requests.Count);
        }
        finally { lifetime.StopApplication(); await run; }
    }

    [PostgresFact]
    public async Task RealHost_RestartDeliversPersistedOutboxWithPollingDisabled()
    {
        await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default);
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: false);
        builder.Configuration["OddsApi:Enabled"] = "false";
        using var host = BuildTestHost(builder);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = host.RunAsync(timeout.Token);
        try
        {
            await WaitForSentAsync(1, timeout.Token);
            Assert.Empty(odds.Requests);
            Assert.Single(discord.Messages);
        }
        finally { lifetime.StopApplication(); await run; }
    }

    [PostgresFact]
    public async Task RealHost_MalformedPaidResponseStopsWithoutRetrying()
    {
        var odds = new OddsHandler { Malformed = true };
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: false);
        using var host = BuildTestHost(builder);
        var worker = host.Services.GetRequiredService<OddsPollingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.RunAsync(timeout.Token);
        Assert.Equal(1, worker.ExitCode);
        Assert.Single(odds.Requests);
        Assert.Empty(discord.Messages);
    }

    [PostgresFact]
    public async Task RealHost_InvalidMarketStopsStartupBeforeAnyPaidRequest()
    {
        var odds = new OddsHandler();
        var discord = new DiscordHandler();
        var builder = await BuilderAsync(odds, discord, once: true);
        builder.Configuration["OddsApi:Markets:0"] = "player_pass_yds";
        using var host = BuildTestHost(builder);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Empty(odds.Requests);
        Assert.Empty(discord.Messages);
    }

    private async Task<HostApplicationBuilder> BuilderAsync(OddsHandler odds, DiscordHandler discord, bool once)
    {
        var builder = ApplicationHost.CreateBuilder(once ? ["--once"] : []);
        builder.Logging.ClearProviders();
        await using var db = await Factory.CreateDbContextAsync();
        builder.Configuration["ConnectionStrings:Postgres"] = db.Database.GetConnectionString();
        builder.Configuration["OddsApi:Enabled"] = "true";
        builder.Configuration["OddsApi:ApiKey"] = "synthetic-test-key";
        builder.Configuration["OddsApi:EnabledSports"] = "americanfootball_nfl,americanfootball_ncaaf";
        if (!once) builder.Configuration["Discord:Enabled"] = "true";
        builder.Configuration["Discord:WebhookUrl"] = "https://discord.test/webhook";
        builder.Configuration["Discord:OutboxPollInterval"] = "00:00:00.01";
        builder.Services.AddSingleton<IClock>(new FixedClock());
        builder.Services.AddHttpClient<IOddsProvider, TheOddsApiClient>().ConfigurePrimaryHttpMessageHandler(() => odds);
        builder.Services.AddHttpClient<IDiscordWebhookClient, DiscordWebhookClient>().ConfigurePrimaryHttpMessageHandler(() => discord);
        return builder;
    }

    private static IHost BuildTestHost(HostApplicationBuilder builder)
    {
        var host = builder.Build();
        using var db = host.Services.GetRequiredService<IDbContextFactory<BettingDbContext>>().CreateDbContext();
        Assert.StartsWith("abodds_test_", db.Database.GetDbConnection().Database, StringComparison.Ordinal);
        return host;
    }

    private async Task WaitForSentAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var db = await Factory.CreateDbContextAsync(cancellationToken);
            if (await db.Alerts.CountAsync(value => value.DeliveryStatus == AlertDeliveryStatus.Sent, cancellationToken) == count) return;
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class OddsHandler : HttpMessageHandler
    {
        private static readonly string[] MlbBookKeys = ["pinnacle", "betonlineag", "fanduel"];
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public bool Malformed { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var sport = uri.Segments[^2].TrimEnd('/');
            var game = Batch().Events.Single();
            var payload = new[]
            {
                new
                {
                    id = sport, sport_key = sport, home_team = game.HomeTeam, away_team = game.AwayTeam,
                    commence_time = game.CommenceTimeUtc,
                    bookmakers = game.Quotes.GroupBy(quote => quote.BookmakerKey).Select(book => new
                    {
                        key = book.Key, title = book.First().BookmakerTitle, last_update = Now,
                        markets = new[] { new { key = "spreads", last_update = Now,
                            outcomes = book.Select(quote => new { name = quote.SelectionDisplayName, price = quote.DecimalOdds, point = quote.Line }) } }
                    })
                }
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Malformed ? "not-json" : sport == "baseball_mlb" ? MlbResponse() : JsonSerializer.Serialize(payload))
            };
            response.Headers.Add("x-requests-remaining", "1000");
            response.Headers.Add("x-requests-used", "3");
            response.Headers.Add("x-requests-last", "3");
            return Task.FromResult(response);
        }

        private static string MlbResponse()
        {
            // Synthetic decimal prices using the provider's documented MLB response shape.
            // Distinct event IDs must preserve both games when the teams play a doubleheader.
            return JsonSerializer.Serialize(Enumerable.Range(1, 2).Select(game => new
            {
                id = "mlb-game-" + game,
                sport_key = "baseball_mlb",
                home_team = "Miami Marlins",
                away_team = "Arizona Diamondbacks",
                commence_time = Now.AddHours(game * 2),
                bookmakers = MlbBookKeys.Select(book => new
                {
                    key = book,
                    title = book,
                    last_update = Now,
                    markets = new[]
                    {
                        new { key = "h2h", outcomes = new[]
                        {
                            new { name = "Arizona Diamondbacks", price = book == "fanduel" ? 2m : 1.8m, point = (decimal?)null },
                            new { name = "Miami Marlins", price = book == "fanduel" ? 1.7m : 2.1m, point = (decimal?)null }
                        } },
                        new { key = "spreads", outcomes = new[]
                        {
                            new { name = "Arizona Diamondbacks", price = book == "fanduel" ? 2m : 1.8m, point = (decimal?)-1.5m },
                            new { name = "Miami Marlins", price = book == "fanduel" ? 1.7m : 2.1m, point = (decimal?)1.5m }
                        } },
                        new { key = "totals", outcomes = new[]
                        {
                            new { name = "Over", price = book == "fanduel" ? 2m : 1.8m, point = (decimal?)7.5m },
                            new { name = "Under", price = book == "fanduel" ? 1.7m : 2.1m, point = (decimal?)7.5m }
                        } }
                    }
                })
            }));
        }
    }

    private sealed class DiscordHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Messages.Enqueue(json.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!);
            return new(HttpStatusCode.NoContent);
        }
    }
}
