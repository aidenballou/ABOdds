using System.Net;
using System.Text;
using System.Text.Json;
using ABOdds.Configuration;
using ABOdds.Services.Alerts;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Alerts;

public sealed class DiscordWebhookClientTests
{
    private static readonly Uri WebhookUri = new("https://discord.test/api/webhooks/1/token");

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task SendAsync_AnyTwoHundredResponse_IsSuccessful(HttpStatusCode statusCode)
    {
        string? requestBody = null;
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestUri = request.RequestUri;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode);
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.SendAsync("test alert", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsRateLimited);
        Assert.Null(result.RetryAfter);
        Assert.Equal(new Uri(WebhookUri + "?wait=true"), requestUri);

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("test alert", payload.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString());
        Assert.Equal("ABOdds Tests", payload.RootElement.GetProperty("username").GetString());
        Assert.Equal(0x2ECC71, payload.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.Empty(payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task SendAsync_RateLimitedResponse_RepresentsJsonRetryAfter()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    "{\"retry_after\":1.25,\"global\":false}",
                    Encoding.UTF8,
                    "application/json")
            }));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.SendAsync("test alert", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(1.25), result.RetryAfter);
    }

    [Fact]
    public async Task SendAsync_ServerError_IsNeitherSuccessfulNorRateLimited()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.SendAsync("test alert", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRateLimited);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData("0", "1.25", 1.25)]
    [InlineData("1", "1.25", null)]
    [InlineData("0", "invalid", null)]
    public async Task SendAsync_SuccessfulResponse_ReportsExhaustedBucket(string remaining, string reset, double? expected)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", reset);
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        var result = await CreateClient(httpClient).SendAsync("test alert", default);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected.HasValue ? TimeSpan.FromSeconds(expected.Value) : (TimeSpan?)null, result.Cooldown);
    }

    [Theory]
    [InlineData("Retry-After")]
    [InlineData("X-RateLimit-Reset-After")]
    public async Task SendAsync_RateLimited_HandlesFractionalHeaders(string header)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation(header, "2.75");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        var result = await CreateClient(httpClient).SendAsync("test alert", default);
        Assert.Equal(TimeSpan.FromSeconds(2.75), result.RetryAfter);
    }

    private static DiscordWebhookClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            Options.Create(new DiscordOptions
            {
                WebhookUrl = WebhookUri.ToString(),
                Username = "ABOdds Tests"
            }));

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request, cancellationToken);
    }
}
