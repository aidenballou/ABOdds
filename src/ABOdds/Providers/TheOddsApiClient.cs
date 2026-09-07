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
    private readonly Uri _baseUri;
    private readonly string _apiKey;
    private readonly string[] _markets;
    private readonly string[] _bookmakers;
    public int EstimatedRequestCost => _markets.Length * ((_bookmakers.Length + 9) / 10);

    public TheOddsApiClient(
        HttpClient httpClient,
        IOptions<OddsApiOptions> oddsApiOptions,
        OddsRequest request,
        IClock clock,
        ILogger<TheOddsApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(oddsApiOptions);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clock);

        var options = oddsApiOptions.Value;
        _httpClient = httpClient;
        _clock = clock;
        _logger = logger;
        _baseUri = new Uri(options.BaseUrl.TrimEnd('/') + '/');
        _apiKey = options.ApiKey;
        _markets = request.MarketKeys.ToArray();
        _bookmakers = request.BookmakerKeys.Select(key => key.Trim().ToLowerInvariant()).ToArray();

        _httpClient.Timeout = options.RequestTimeout;
    }

    public async Task<NormalizedOddsBatch> GetOddsAsync(
        string sportKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sportKey);

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

        return OddsNormalizer.Normalize(sportKey, _clock.UtcNow, sourceEvents, quota);
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
