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
        Assert.Equal(WebhookUri, requestUri);

        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("test alert", payload.RootElement.GetProperty("content").GetString());
        Assert.Equal("ABOdds Tests", payload.RootElement.GetProperty("username").GetString());
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
