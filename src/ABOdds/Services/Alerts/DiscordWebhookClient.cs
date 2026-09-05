using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ABOdds.Configuration;
using Microsoft.Extensions.Options;

namespace ABOdds.Services.Alerts;

public sealed record DiscordWebhookResult(HttpStatusCode StatusCode, TimeSpan? RetryAfter)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
}

public interface IDiscordWebhookClient
{
    Task<DiscordWebhookResult> SendAsync(string content, CancellationToken cancellationToken);
}

public sealed class DiscordWebhookClient : IDiscordWebhookClient
{
    private readonly HttpClient _httpClient;
    private readonly DiscordOptions _options;

    public DiscordWebhookClient(HttpClient httpClient, IOptions<DiscordOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = _options.RequestTimeout;
    }

    public async Task<DiscordWebhookResult> SendAsync(
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        using var response = await _httpClient.PostAsJsonAsync(
            _options.WebhookUrl,
            new DiscordWebhookPayload(content, _options.Username),
            cancellationToken);

        var retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
            ? await ReadRetryAfterAsync(response, cancellationToken)
            : null;

        return new DiscordWebhookResult(response.StatusCode, retryAfter);
    }

    private static async Task<TimeSpan?> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (TryReadSecondsHeader(response, "Retry-After", out var retryAfter) ||
            TryReadSecondsHeader(response, "X-RateLimit-Reset-After", out retryAfter))
        {
            return retryAfter;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("retry_after", out var element))
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var seconds))
            {
                return ToDuration(seconds);
            }

            if (element.ValueKind == JsonValueKind.String &&
                decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out seconds))
            {
                return ToDuration(seconds);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool TryReadSecondsHeader(
        HttpResponseMessage response,
        string headerName,
        out TimeSpan retryAfter)
    {
        retryAfter = default;
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        var value = values.FirstOrDefault();
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var duration = ToDuration(seconds);
        if (!duration.HasValue)
        {
            return false;
        }

        retryAfter = duration.Value;
        return true;
    }

    private static TimeSpan? ToDuration(decimal seconds)
    {
        if (seconds < 0m)
        {
            return null;
        }

        return TimeSpan.FromSeconds((double)seconds);
    }

    private sealed record DiscordWebhookPayload(string Content, string Username);
}
