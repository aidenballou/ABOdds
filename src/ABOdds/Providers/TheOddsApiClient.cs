using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Providers;

public sealed class TheOddsApiClient : IOddsProvider
{
    private static readonly Action<ILogger, int, int, int, Exception?> LogQuota = LoggerMessage.Define<int, int, int>(
        LogLevel.Information, new EventId(2100, nameof(LogQuota)),
        "Odds API response: credits remaining {Remaining}, used {Used}, request cost {Cost}");
    private readonly ILogger<TheOddsApiClient> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly IOddsNormalizer _normalizer;
    private readonly Uri _baseUri;
    private readonly string _apiKey;
    private readonly string[] _markets;
    private readonly string[] _bookmakers;
    public int EstimatedRequestCost => _markets.Length * ((_bookmakers.Length + 9) / 10);

    public TheOddsApiClient(
        HttpClient httpClient,
        IOptions<OddsApiOptions> oddsApiOptions,
        IOptions<FairValueOptions> fairValueOptions,
        IOptions<EvOptions> evOptions,
        IClock clock,
        IOddsNormalizer normalizer,
        ILogger<TheOddsApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(oddsApiOptions);
        ArgumentNullException.ThrowIfNull(fairValueOptions);
        ArgumentNullException.ThrowIfNull(evOptions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(normalizer);

        var options = oddsApiOptions.Value;
        _httpClient = httpClient;
        _clock = clock;
        _normalizer = normalizer;
        _logger = logger;
        _baseUri = CreateBaseUri(options.BaseUrl);
        _apiKey = options.ApiKey;
        _markets = GetMarkets(options.Markets);
        _bookmakers = GetBookmakers(fairValueOptions.Value, evOptions.Value);

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("OddsApi:RequestTimeout must be positive.");
        }

        _httpClient.Timeout = options.RequestTimeout;
    }

    public async Task<NormalizedOddsBatch> GetOddsAsync(
        string sportKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sportKey);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("OddsApi:ApiKey is required when the provider is enabled.");
        }

        if (_bookmakers.Length == 0)
        {
            throw new InvalidOperationException("At least one reference or target bookmaker is required.");
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(_httpClient.Timeout);
        using var response = await _httpClient.GetAsync(
            CreateRequestUri(sportKey),
            HttpCompletionOption.ResponseHeadersRead,
            request.Token);

        var quota = new ApiQuotaSnapshot(
            ReadIntHeader(response, "x-requests-remaining"),
            ReadIntHeader(response, "x-requests-used"),
            ReadIntHeader(response, "x-requests-last"));
        LogQuota(_logger, quota.Remaining ?? -1, quota.Used ?? -1, quota.LastRequestCost ?? -1, null);
        response.EnsureSuccessStatusCode();

        var sourceEvents = await response.Content.ReadFromJsonAsync<List<TheOddsApiEventDto>>(
            SerializerOptions,
            request.Token) ?? [];

        return _normalizer.Normalize(sportKey, _clock.UtcNow, sourceEvents, quota);
    }

    private Uri CreateRequestUri(string sportKey)
    {
        var query = string.Join('&',
            $"apiKey={Uri.EscapeDataString(_apiKey)}",
            $"markets={Uri.EscapeDataString(string.Join(',', _markets))}",
            $"bookmakers={Uri.EscapeDataString(string.Join(',', _bookmakers))}",
            $"commenceTimeFrom={Uri.EscapeDataString(_clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}",
            "oddsFormat=decimal",
            "dateFormat=iso");

        return new Uri(
            _baseUri,
            $"sports/{Uri.EscapeDataString(sportKey.Trim())}/odds?{query}");
    }

    private static Uri CreateBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("OddsApi:BaseUrl must be an absolute URL.");
        }

        return baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
    }

    private static string[] GetMarkets(IEnumerable<string> configuredMarkets)
    {
        var markets = configuredMarkets
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (markets.Length == 0)
        {
            throw new InvalidOperationException("OddsApi:Markets must contain at least one market.");
        }

        if (markets.Any(static market =>
                market is not MarketKeys.Moneyline and
                not MarketKeys.Spread and
                not MarketKeys.Total))
        {
            throw new InvalidOperationException(
                "OddsApi:Markets only supports h2h, spreads, and totals in v1.");
        }

        return markets;
    }

    private static string[] GetBookmakers(
        FairValueOptions fairValueOptions,
        EvOptions evOptions) =>
        fairValueOptions.ReferenceBooks
            .Select(static bookmaker => bookmaker.Key)
            .Concat(evOptions.TargetBooks.Select(static bookmaker => bookmaker.Key))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static int? ReadIntHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        return int.TryParse(
            values.FirstOrDefault(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}
