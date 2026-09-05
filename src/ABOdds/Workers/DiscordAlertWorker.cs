using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed class DiscordAlertWorker(
    AlertRepository alertRepository,
    IDiscordWebhookClient discordClient,
    IOptions<DiscordOptions> discordOptions,
    IOptions<PollingOptions> pollingOptions,
    SemaphoreSlim alertStateGate,
    IClock clock,
    ILogger<DiscordAlertWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDisabled = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3001, nameof(LogDisabled)),
        "Discord delivery is disabled; pending alerts will remain in the outbox");

    private static readonly Action<ILogger, Guid, Exception?> LogSent = LoggerMessage.Define<Guid>(
        LogLevel.Information,
        new EventId(3002, nameof(LogSent)),
        "Delivered Discord alert {AlertId}");

    private static readonly Action<ILogger, Guid, DateTimeOffset, string, Exception?> LogRetry =
        LoggerMessage.Define<Guid, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(3003, nameof(LogRetry)),
            "Discord alert {AlertId} will retry at {NextAttemptAtUtc}: {Reason}");

    private static readonly Action<ILogger, Guid, string, Exception?> LogPermanentFailure =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(3004, nameof(LogPermanentFailure)),
            "Discord alert {AlertId} failed permanently: {Reason}");

    private static readonly Action<ILogger, Guid, string, Exception?> LogExpired =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(3005, nameof(LogExpired)),
            "Discord alert {AlertId} expired before delivery: {Reason}");

    private static readonly Action<ILogger, Exception?> LogOutboxFailure = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(3006, nameof(LogOutboxFailure)),
        "Discord outbox processing failed; retrying");

    private readonly DiscordOptions _options = discordOptions.Value;
    private readonly TimeSpan _maximumSourceAge = pollingOptions.Value.MaximumSourceAge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(logger, null);
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PendingAlert? alert = null;
                await alertStateGate.WaitAsync(stoppingToken);
                try
                {
                    alert = await alertRepository.GetNextPendingAsync(clock.UtcNow, stoppingToken);
                }
                finally
                {
                    alertStateGate.Release();
                }

                if (alert is not null)
                {
                    await DeliverAsync(alert, stoppingToken);
                }
                else
                {
                    await WaitForWorkAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogOutboxFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DeliverAsync(PendingAlert alert, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        string? expirationReason = null;
        if (alert.CommenceTimeUtc <= now)
        {
            expirationReason = "the event has started";
        }
        else if (!alert.StateIsActive || alert.StateLastSeenAtUtc > alert.ObservedAtUtc)
        {
            expirationReason = "a newer market snapshot replaced it";
        }
        else if (alert.OldestSourceUpdatedAtUtc <= now - _maximumSourceAge)
        {
            expirationReason = "the source odds are stale";
        }

        if (expirationReason is not null)
        {
            await UpdateStateAsync(() => alertRepository.MarkExpiredAsync(alert.Id, expirationReason, cancellationToken), cancellationToken);
            LogExpired(logger, alert.Id, expirationReason, null);
            return;
        }

        try
        {
            var remainingValidity = GetDeadline(alert) - clock.UtcNow;
            if (remainingValidity <= TimeSpan.Zero)
            {
                await UpdateStateAsync(() => alertRepository.MarkExpiredAsync(alert.Id, "the delivery deadline passed", cancellationToken), cancellationToken);
                return;
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(remainingValidity < _options.RequestTimeout ? remainingValidity : _options.RequestTimeout);
            var result = await discordClient.SendAsync(alert.Message, attempt.Token);
            if (result.IsSuccess)
            {
                var sentAt = clock.UtcNow;
                var cooldownUntil = result.Cooldown is { } cooldown
                    ? sentAt + cooldown + TimeSpan.FromMilliseconds(250)
                    : (DateTimeOffset?)null;
                await UpdateStateAsync(() => alertRepository.MarkSentAsync(
                    alert.Id, sentAt, cancellationToken, cooldownUntil), cancellationToken);
                LogSent(logger, alert.Id, null);
                return;
            }

            if (result.IsRateLimited)
            {
                var delay = result.RetryAfter ?? GetExponentialRetryDelay(alert.DeliveryAttempts);
                if (result.Cooldown > delay) delay = result.Cooldown.Value;
                delay += TimeSpan.FromMilliseconds(250);
                var until = clock.UtcNow + (delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay);
                const string reason = "Discord returned HTTP 429.";
                await UpdateStateAsync(() => alertRepository.ScheduleRetryAsync(
                    alert.Id, until, reason, cancellationToken, rateLimited: true), cancellationToken);
                LogRetry(logger, alert.Id, until, reason, null);
                return;
            }

            if ((int)result.StatusCode >= 500)
            {
                var reason = $"Discord returned HTTP {(int)result.StatusCode}.";
                await RetryAsync(alert, result.RetryAfter, reason, cancellationToken);
                return;
            }

            var permanentReason = $"Discord returned HTTP {(int)result.StatusCode}.";
            await UpdateStateAsync(() => alertRepository.MarkFailedAsync(alert.Id, permanentReason, cancellationToken), cancellationToken);
            LogPermanentFailure(logger, alert.Id, permanentReason, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RetryAsync(alert, null, exception.Message, cancellationToken);
        }
    }

    private async Task RetryAsync(
        PendingAlert alert,
        TimeSpan? requestedDelay,
        string reason,
        CancellationToken cancellationToken)
    {
        var retryDelay = requestedDelay ?? GetExponentialRetryDelay(alert.DeliveryAttempts);
        if (retryDelay < TimeSpan.FromSeconds(1))
        {
            retryDelay = TimeSpan.FromSeconds(1);
        }

        var now = clock.UtcNow;
        var nextAttempt = now + retryDelay;
        if (nextAttempt >= GetDeadline(alert))
        {
            const string expired = "a retry cannot complete before the odds expire or kickoff";
            await UpdateStateAsync(() => alertRepository.MarkExpiredAsync(alert.Id, expired, cancellationToken), cancellationToken);
            LogExpired(logger, alert.Id, expired, null);
            return;
        }

        await UpdateStateAsync(() => alertRepository.ScheduleRetryAsync(alert.Id, nextAttempt, reason, cancellationToken), cancellationToken);
        LogRetry(logger, alert.Id, nextAttempt, reason, null);
    }

    private DateTimeOffset GetDeadline(PendingAlert alert)
    {
        var sourceExpiration = alert.OldestSourceUpdatedAtUtc + _maximumSourceAge;
        return sourceExpiration < alert.CommenceTimeUtc ? sourceExpiration : alert.CommenceTimeUtc;
    }

    private async Task UpdateStateAsync(Func<Task> update, CancellationToken cancellationToken)
    {
        await alertStateGate.WaitAsync(cancellationToken);
        try { await update(); }
        finally { alertStateGate.Release(); }
    }

    private TimeSpan GetExponentialRetryDelay(int priorAttempts)
    {
        var exponent = Math.Min(priorAttempts, 10);
        var seconds = Math.Pow(2, exponent + 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaximumRetryDelay.TotalSeconds));
    }

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(_options.OutboxPollInterval, stoppingToken);
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
