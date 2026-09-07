using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.NoSweat;
using ABOdds.Pipeline;
using ABOdds.Providers;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.NoSweat;

public sealed class NoSweatHostTests
{
    [Fact]
    public async Task RunOnce_RequestsOnlyNoSweatBooksAndMarketsWithoutEvOrDatabaseServices()
    {
        var http = new RecordingHttp();
        var logs = new RecordingLogs();
        var builder = Builder(http, logs, once: true);
        builder.Configuration["FairValue:ReferenceBooks:0:Key"] = "invalid-ev-reference";
        builder.Configuration["Ev:MinimumExpectedValue"] = "-1";
        builder.Configuration["ConnectionStrings:Postgres"] = "";
        using var host = builder.Build();
        Assert.Null(host.Services.GetService<OddsPipeline>());
        Assert.Null(host.Services.GetService<OddsPollingWorker>());
        Assert.Null(host.Services.GetService<AlertRepository>());
        Assert.Null(host.Services.GetService<DiscordAlertWorker>());
        Assert.Null(host.Services.GetService<IDbContextFactory<BettingDbContext>>());
        Assert.IsType<NoSweatWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
        var worker = host.Services.GetRequiredService<NoSweatWorker>();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, worker.ExitCode);
        Assert.Equal(3, http.OddsRequests.Count);
        Assert.Empty(http.DiscordMessages);
        Assert.All(http.OddsRequests, uri =>
        {
            var query = Uri.UnescapeDataString(uri.Query);
            Assert.Contains("markets=spreads,totals", query, StringComparison.Ordinal);
            Assert.Contains("bookmakers=betmgm,fanduel,draftkings,williamhill_us,fanatics,hardrockbet,espnbet", query, StringComparison.Ordinal);
            Assert.DoesNotContain("pinnacle", query, StringComparison.Ordinal);
        });
        Assert.Contains(logs.Messages, value => value.Contains("Projected minimum profit: $375.00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MlbOnly_RequestsNoFootballInNoSweatMode()
    {
        var http = new RecordingHttp();
        var logs = new RecordingLogs();
        var builder = Builder(http, logs, once: true);
        builder.Configuration["OddsApi:EnabledSports"] = "baseball_mlb";
        using var host = builder.Build();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("/v4/sports/baseball_mlb/odds", Assert.Single(http.OddsRequests).AbsolutePath);
        Assert.Contains(logs.Messages, message => message.Contains("· MLB\nStart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuousMode_PublishesOnlyNoSweatReportsThroughDiscordAdapter()
    {
        var http = new RecordingHttp();
        var builder = Builder(http, new RecordingLogs(), once: false);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = host.RunAsync(timeout.Token);
        try
        {
            var message = await http.Delivered.Task.WaitAsync(timeout.Token);
            Assert.Contains("NO-SWEAT", message, StringComparison.Ordinal);
            Assert.Contains("Projected minimum profit", message, StringComparison.Ordinal);
            Assert.DoesNotContain("EV**", message, StringComparison.Ordinal);
            Assert.DoesNotContain("no-vig", message, StringComparison.Ordinal);
        }
        finally { lifetime.StopApplication(); await run; }
    }

    [Fact]
    public async Task BonusStage_OptimizesExistingIndivisibleBonusUsingCurrentCash()
    {
        var http = new RecordingHttp();
        var logs = new RecordingLogs();
        var builder = Builder(http, logs, once: true);
        builder.Configuration["NoSweat:Stage"] = "BonusConversion";
        builder.Configuration["NoSweat:BonusBetAmount"] = "300";
        builder.Configuration["NoSweat:Bankroll"] = "100";
        using var host = builder.Build();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(logs.Messages, value => value.Contains("Minimum conversion cash: $100.00", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, value => value.Contains("Bonus stake: $300.00; cash hedge: $100.00", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, value => value.Contains("Projected minimum profit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreditCap_PreventsUnaffordablePaidRequest()
    {
        var http = new RecordingHttp();
        var builder = Builder(http, new RecordingLogs(), once: true);
        builder.Configuration["Polling:MaximumCreditsPerRun"] = "1";
        using var host = builder.Build();
        var worker = host.Services.GetRequiredService<NoSweatWorker>();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(http.OddsRequests);
        Assert.Equal(0, worker.ExitCode);
    }

    [Fact]
    public async Task CreditCap_StillReportsTheSnapshotAlreadyPurchased()
    {
        var http = new RecordingHttp();
        var logs = new RecordingLogs();
        var builder = Builder(http, logs, once: true);
        builder.Configuration["Polling:MaximumCreditsPerRun"] = "2";
        using var host = builder.Build();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(http.OddsRequests);
        Assert.Contains(logs.Messages, message => message.Contains("Projected minimum profit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingQuota_StopsBeforeASecondRequest()
    {
        var http = new RecordingHttp { IncludeQuota = false };
        using var host = Builder(http, new RecordingLogs(), once: true).Build();
        var worker = host.Services.GetRequiredService<NoSweatWorker>();
        await host.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(http.OddsRequests);
        Assert.Equal(1, worker.ExitCode);
    }

    [Theory]
    [InlineData("PromotionLimit", "0")]
    [InlineData("Bankroll", "-1")]
    [InlineData("BonusBetAmount", "0.001")]
    [InlineData("Stage", "99")]
    [InlineData("MinimumQualifyingDecimalOdds", "0.5")]
    [InlineData("HedgeBooks:0:Key", " betmgm ")]
    [InlineData("HedgeBooks:1:Key", " FANDUEL ")]
    public async Task InvalidNoSweatSettings_StopBeforeHttp(string key, string value)
    {
        var http = new RecordingHttp();
        var builder = Builder(http, new RecordingLogs(), once: true);
        builder.Configuration["NoSweat:" + key] = value;
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Empty(http.OddsRequests);
        Assert.Empty(http.DiscordMessages);
    }

    [Fact]
    public void DisabledMode_RegistersOnlyTheExistingEvWorkers()
    {
        using var host = ApplicationHost.CreateBuilder(["--NoSweat:Enabled=false"]).Build();
        Assert.Null(host.Services.GetService<NoSweatWorker>());
        Assert.NotNull(host.Services.GetService<OddsPollingWorker>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is DiscordAlertWorker);
    }

    private static HostApplicationBuilder Builder(RecordingHttp http, RecordingLogs logs, bool once)
    {
        var args = new List<string> { "--NoSweat:Enabled=true", "--OddsApi:Enabled=true", "--OddsApi:ApiKey=synthetic" };
        if (once) args.Add("--once");
        var builder = ApplicationHost.CreateBuilder(args.ToArray());
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration["Discord:Enabled"] = once ? "false" : "true";
        builder.Configuration["Discord:WebhookUrl"] = "https://discord.test/webhook";
        builder.Services.AddSingleton<IClock>(new FixedClock());
        builder.Services.AddHttpClient<IOddsProvider, TheOddsApiClient>().ConfigurePrimaryHttpMessageHandler(() => http);
        builder.Services.AddHttpClient<IDiscordWebhookClient, DiscordWebhookClient>().ConfigurePrimaryHttpMessageHandler(() => http);
        return builder;
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => NoSweatTestData.Now; }

    private sealed class RecordingHttp : HttpMessageHandler
    {
        public bool IncludeQuota { get; init; } = true;
        public ConcurrentQueue<Uri> OddsRequests { get; } = new();
        public ConcurrentQueue<string> DiscordMessages { get; } = new();
        public TaskCompletionSource<string> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var message = json.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!;
                DiscordMessages.Enqueue(message);
                Delivered.TrySetResult(message);
                return new(HttpStatusCode.OK);
            }
            OddsRequests.Enqueue(request.RequestUri!);
            var game = NoSweatTestData.Market().Event;
            var body = new[] { new
            {
                id = request.RequestUri!.AbsolutePath, home_team = game.HomeTeam, away_team = game.AwayTeam,
                commence_time = game.CommenceTimeUtc,
                bookmakers = game.Quotes.Select(quote => new
                {
                    key = quote.BookmakerKey, title = quote.BookmakerTitle, last_update = quote.SourceUpdatedAtUtc,
                    markets = new[] { new { key = quote.MarketKey, outcomes = new[] {
                        new { name = quote.SelectionDisplayName, price = quote.DecimalOdds, point = quote.Line } } } }
                })
            } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
            if (IncludeQuota)
            {
                response.Headers.Add("x-requests-remaining", "1000");
                response.Headers.Add("x-requests-used", "2");
                response.Headers.Add("x-requests-last", "2");
            }
            return response;
        }
    }

    private sealed class RecordingLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
