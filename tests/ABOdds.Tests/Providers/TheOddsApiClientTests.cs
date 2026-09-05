using System.Net;
using System.Text;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Providers;
using ABOdds.Time;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace ABOdds.Tests.Providers;

public sealed class TheOddsApiClientTests
{
    [Fact]
    public async Task ResponseBodyThatStalls_IsCancelledByRequestTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new StalledBodyHandler());
        var client = new TheOddsApiClient(http,
            Options.Create(new OddsApiOptions { ApiKey = "synthetic", Markets = ["h2h"], RequestTimeout = TimeSpan.FromMilliseconds(50) }),
            CreateFairValueOptions(), CreateEvOptions(), new FixedClock(Now), NullLogger<TheOddsApiClient>.Instance);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetOddsAsync("americanfootball_nfl", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(cancellation.IsCancellationRequested);
        }
        finally { await cancellation.CancelAsync(); }
    }

    [Fact]
    public async Task MalformedResponse_LogsQuotaBeforeDeserializationWithoutApiKey()
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new RecordingHandler("not-json"));
        var client = new TheOddsApiClient(http,
            Options.Create(new OddsApiOptions { ApiKey = "do-not-log-this-key", Markets = ["h2h"] }),
            CreateFairValueOptions(), CreateEvOptions(), new FixedClock(Now), logger);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.GetOddsAsync("americanfootball_nfl"));
        Assert.Contains("credits remaining 997", Assert.Single(logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-key", logger.Messages.Single(), StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 22, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetOddsAsync_RequestsConfiguredUnionAndCapturesQuota()
    {
        var handler = new RecordingHandler(
            """
            [{
              "id": "event-1",
              "sport_key": "americanfootball_nfl",
              "sport_title": "NFL",
              "commence_time": "2026-09-04T00:00:00Z",
              "home_team": "Houston Texans",
              "away_team": "Indianapolis Colts",
              "bookmakers": [{
                "key": "fanduel",
                "title": "FanDuel",
                "last_update": "2026-09-03T22:14:00Z",
                "markets": [{
                  "key": "spreads",
                  "last_update": "2026-09-03T22:14:30Z",
                  "outcomes": [
                    { "name": "Indianapolis Colts", "price": 1.9524, "point": 3.5 },
                    { "name": "Houston Texans", "price": 1.8696, "point": -3.5 }
                  ]
                }]
              }]
            }]
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GetOddsAsync("americanfootball_nfl", CancellationToken.None);

        var query = ParseQuery(Assert.IsType<Uri>(handler.RequestUri));
        Assert.Equal("secret key", query["apiKey"]);
        Assert.Equal("h2h,spreads,totals", query["markets"]);
        Assert.Equal("pinnacle,betonlineag,fanduel", query["bookmakers"]);
        Assert.Equal("decimal", query["oddsFormat"]);
        Assert.Equal("iso", query["dateFormat"]);
        Assert.Equal("2026-09-03T22:15:00Z", query["commenceTimeFrom"]);
        Assert.EndsWith(
            "/v4/sports/americanfootball_nfl/odds",
            handler.RequestUri!.AbsolutePath,
            StringComparison.Ordinal);
        Assert.Equal(new ApiQuotaSnapshot(997, 3, 3), result.Quota);
        Assert.Equal(Now, result.ObservedAtUtc);
        Assert.Equal(2, Assert.Single(result.Events).Quotes.Count);
    }

    private static TheOddsApiClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        Options.Create(new OddsApiOptions
        {
            BaseUrl = "https://example.test/v4",
            ApiKey = "secret key",
            Markets = ["h2h", "spreads", "totals"]
        }),
        CreateFairValueOptions(),
        CreateEvOptions(),
        new FixedClock(Now),
        NullLogger<TheOddsApiClient>.Instance);

    private static IOptions<FairValueOptions> CreateFairValueOptions() =>
        Options.Create(new FairValueOptions
        {
            ReferenceBooks =
            [
                new BookOptions { Key = "Pinnacle" },
                new BookOptions { Key = "betonlineag" }
            ]
        });

    private static IOptions<EvOptions> CreateEvOptions() =>
        Options.Create(new EvOptions
        {
            TargetBooks =
            [
                new BookOptions { Key = " FanDuel " }
            ]
        });

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static pair => Uri.UnescapeDataString(pair[0]),
                static pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-requests-remaining", "997");
            response.Headers.Add("x-requests-used", "3");
            response.Headers.Add("x-requests-last", "3");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingLogger : ILogger<TheOddsApiClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
