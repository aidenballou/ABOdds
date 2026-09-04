using System.Net;
using System.Text;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Providers;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Providers;

public sealed class TheOddsApiClientTests
{
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
        Assert.EndsWith(
            "/v4/sports/americanfootball_nfl/odds",
            handler.RequestUri!.AbsolutePath,
            StringComparison.Ordinal);
        Assert.Equal(new ApiQuotaSnapshot(997, 3, 3), result.Quota);
        Assert.Equal(Now, result.ObservedAtUtc);
        Assert.Equal(2, Assert.Single(result.Events).Quotes.Count);
    }

    [Fact]
    public void Constructor_RejectsUnsupportedConfiguredMarket()
    {
        using var httpClient = new HttpClient(new RecordingHandler("[]"));
        var oddsOptions = Options.Create(new OddsApiOptions
        {
            ApiKey = "key",
            Markets = ["h2h", "player_pass_yds"]
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new TheOddsApiClient(
            httpClient,
            oddsOptions,
            CreateFairValueOptions(),
            CreateEvOptions(),
            new FixedClock(Now),
            new OddsNormalizer()));

        Assert.Contains("only supports h2h, spreads, and totals", exception.Message, StringComparison.Ordinal);
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
        new OddsNormalizer());

    private static IOptions<FairValueOptions> CreateFairValueOptions() =>
        Options.Create(new FairValueOptions
        {
            ReferenceBooks =
            [
                new ReferenceBookOptions { Key = "Pinnacle", Weight = 0.6m },
                new ReferenceBookOptions { Key = "betonlineag", Weight = 0.4m }
            ]
        });

    private static IOptions<EvOptions> CreateEvOptions() =>
        Options.Create(new EvOptions
        {
            TargetBooks =
            [
                new TargetBookOptions { Key = "FanDuel" },
                new TargetBookOptions { Key = "pinnacle" }
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
}
