using System.Net;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Persistence;

public sealed class DiscordWorkerTests : PostgresTest
{
    [PostgresFact]
    public async Task RateLimit_PausesAllMessagesAndNewAlertsAcrossRestart()
    {
        var batch = Batch();
        var game = batch.Events.Single();
        batch = batch with { Events = [game, game with { ProviderEventId = "event-2" }] };
        Assert.Equal(2, (await Alerts.ApplyRulesAsync(await ProcessAsync(batch), default)).Count);
        var client = new RateLimitedWebhook();
        using var gate = new SemaphoreSlim(1, 1);
        using (var worker = CreateWorker(client, gate))
        {
            await worker.StartAsync(default);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await using var db = await Factory.CreateDbContextAsync();
                    if (await db.Alerts.AnyAsync(value => value.DeliveryAttempts > 0, timeout.Token)) break;
                    await Task.Delay(10, timeout.Token);
                }
                await Task.Delay(100);
                Assert.Equal(1, client.Calls);
            }
            finally { await worker.StopAsync(default); }
        }

        var fresh = Batch(Now.AddSeconds(1));
        var freshGame = fresh.Events.Single();
        fresh = fresh with { Events = [freshGame, freshGame with { ProviderEventId = "event-2" }, freshGame with { ProviderEventId = "event-3" }] };
        Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(fresh), default));
        using (var restartedWorker = CreateWorker(client, gate))
        {
            await restartedWorker.StartAsync(default);
            await Task.Delay(100);
            await restartedWorker.StopAsync(default);
        }
        Assert.Equal(1, client.Calls);
        Assert.Null(await Alerts.GetNextPendingAsync(Now.AddSeconds(59), default));
        Assert.NotNull(await Alerts.GetNextPendingAsync(Now.AddSeconds(60.25), default));
    }

    [PostgresFact]
    public async Task SuccessfulSend_ExhaustedBucketPausesOutboxAcrossRestart()
    {
        var batch = Batch();
        batch = batch with { Events = [batch.Events[0], batch.Events[0] with { ProviderEventId = "event-2" }] };
        await Alerts.ApplyRulesAsync(await ProcessAsync(batch), default);
        var client = new SuccessfulWebhook(TimeSpan.FromSeconds(10));
        using var gate = new SemaphoreSlim(1, 1);
        using (var worker = CreateWorker(client, gate))
        {
            await worker.StartAsync(default);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await using var db = await Factory.CreateDbContextAsync();
                    if (await db.Alerts.AnyAsync(value => value.DeliveryStatus == AlertDeliveryStatus.Sent, timeout.Token)) break;
                    await Task.Delay(10, timeout.Token);
                }
            }
            finally { await worker.StopAsync(default); }
        }
        using (var restarted = CreateWorker(client, gate))
        {
            await restarted.StartAsync(default);
            await Task.Delay(100);
            await restarted.StopAsync(default);
        }
        Assert.Equal(1, client.Calls);
        Assert.Null(await Alerts.GetNextPendingAsync(Now.AddSeconds(10), default));
        Assert.NotNull(await Alerts.GetNextPendingAsync(Now.AddSeconds(10.25), default));
    }

    [PostgresFact]
    public async Task RateLimitedAlert_IsDeliveredAfterCooldown()
    {
        var id = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default));
        using var gate = new SemaphoreSlim(1, 1);
        using (var limitedWorker = CreateWorker(new RateLimitedWebhook(), gate))
        {
            await limitedWorker.StartAsync(default);
            try { await WaitForStatusAsync(id, AlertDeliveryStatus.Pending, minimumAttempts: 1); }
            finally { await limitedWorker.StopAsync(default); }
        }
        Assert.Null(await Alerts.GetNextPendingAsync(Now.AddSeconds(60), default));
        var client = new SuccessfulWebhook(null);
        using var worker = CreateWorker(client, gate, now: Now.AddSeconds(60.25));
        await worker.StartAsync(default);
        try { await WaitForStatusAsync(id, AlertDeliveryStatus.Sent, minimumAttempts: 2); }
        finally { await worker.StopAsync(default); }
        Assert.Equal(1, client.Calls);
    }

    [PostgresFact]
    public async Task SlowWebhook_DoesNotLockReconciliation()
    {
        var first = await ProcessAsync(Batch());
        await Alerts.ApplyRulesAsync(first, default);
        var next = await ProcessAsync(Batch(Now.AddSeconds(1), 1.5m));
        var client = new BlockingWebhook();
        using var gate = new SemaphoreSlim(1, 1);
        using var worker = CreateWorker(client, gate);
        await worker.StartAsync(default);
        try
        {
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var acquired = await gate.WaitAsync(TimeSpan.FromMilliseconds(200));
            Assert.True(acquired, "Discord HTTP must not hold the reconciliation gate.");
            try { Assert.Empty(await Alerts.ApplyRulesAsync(next, default)); }
            finally { gate.Release(); }
        }
        finally { await worker.StopAsync(default); }
    }

    [PostgresFact]
    public async Task SendCrossingSourceDeadline_IsCancelledAndExpires()
    {
        var id = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default));
        var client = new BlockingWebhook();
        using var gate = new SemaphoreSlim(1, 1);
        using var worker = CreateWorker(client, gate, TimeSpan.FromMilliseconds(50));
        await worker.StartAsync(default);
        try
        {
            await WaitForStatusAsync(id, AlertDeliveryStatus.Expired);
            Assert.True(client.Cancelled);
        }
        finally { await worker.StopAsync(default); }
    }

    [PostgresFact]
    public async Task SendCrossingKickoff_IsCancelledAndExpires()
    {
        var batch = Batch();
        batch = batch with { Events = [batch.Events.Single() with { CommenceTimeUtc = Now.AddMilliseconds(50) }] };
        var id = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(batch), default));
        var client = new BlockingWebhook();
        using var gate = new SemaphoreSlim(1, 1);
        using var worker = CreateWorker(client, gate);
        await worker.StartAsync(default);
        try
        {
            await WaitForStatusAsync(id, AlertDeliveryStatus.Expired);
            Assert.True(client.Cancelled);
        }
        finally { await worker.StopAsync(default); }
    }

    [PostgresFact]
    public async Task RequestTimeout_CancelsSendAndSchedulesRetryWhileFresh()
    {
        var id = Assert.Single(await Alerts.ApplyRulesAsync(await ProcessAsync(Batch()), default));
        var client = new BlockingWebhook();
        using var gate = new SemaphoreSlim(1, 1);
        using var worker = CreateWorker(client, gate, requestTimeout: TimeSpan.FromMilliseconds(50));
        await worker.StartAsync(default);
        try
        {
            await WaitForStatusAsync(id, AlertDeliveryStatus.Pending, minimumAttempts: 1);
            Assert.True(client.Cancelled);
        }
        finally { await worker.StopAsync(default); }
    }

    private DiscordAlertWorker CreateWorker(IDiscordWebhookClient client, SemaphoreSlim gate,
        TimeSpan? sourceAge = null, TimeSpan? requestTimeout = null, DateTimeOffset? now = null) => new(
        Alerts, client,
        Options.Create(new DiscordOptions { Enabled = true, RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10) }),
        Options.Create(new PollingOptions { MaximumSourceAge = sourceAge ?? TimeSpan.FromSeconds(90) }),
        gate, new FixedClock(now ?? Now), NullLogger<DiscordAlertWorker>.Instance);

    private async Task WaitForStatusAsync(Guid id, AlertDeliveryStatus status, int minimumAttempts = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var db = await Factory.CreateDbContextAsync(timeout.Token);
            var alert = await db.Alerts.SingleAsync(value => value.Id == id, timeout.Token);
            if (alert.DeliveryStatus == status && alert.DeliveryAttempts >= minimumAttempts) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class BlockingWebhook : IDiscordWebhookClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<DiscordWebhookResult> SendAsync(string content, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(HttpStatusCode.NoContent, null);
        }
    }

    private sealed class SuccessfulWebhook(TimeSpan? cooldown) : IDiscordWebhookClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<DiscordWebhookResult> SendAsync(string content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DiscordWebhookResult(HttpStatusCode.OK, null, cooldown));
        }
    }

    private sealed class RateLimitedWebhook : IDiscordWebhookClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<DiscordWebhookResult> SendAsync(string content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DiscordWebhookResult(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(60)));
        }
    }
}
