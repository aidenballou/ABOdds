using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed class DiscordAlertWorker(
    AlertRepository alertRepository,
    IDiscordWebhookClient discordClient,
    PipelineChannels channels,
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
                    if (alert is not null)
                    {
                        await DeliverAsync(alert, stoppingToken);
                    }
                }
                finally
                {
                    alertStateGate.Release();
                }

                if (alert is null)
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
        else if (!alert.StateIsActive || alert.StateLastSeenAtUtc > alert.CreatedAtUtc)
        {
            expirationReason = "a newer market snapshot replaced it";
        }
        else if (alert.OldestSourceUpdatedAtUtc < now - _maximumSourceAge)
        {
            expirationReason = "the source odds are stale";
        }

        if (expirationReason is not null)
        {
            await alertRepository.MarkExpiredAsync(alert.Id, expirationReason, cancellationToken);
            LogExpired(logger, alert.Id, expirationReason, null);
            return;
        }

        try
        {
            var result = await discordClient.SendAsync(alert.Message, cancellationToken);
            if (result.IsSuccess)
            {
                await alertRepository.MarkSentAsync(alert.Id, clock.UtcNow, cancellationToken);
                LogSent(logger, alert.Id, null);
                return;
            }

            if (result.IsRateLimited || (int)result.StatusCode >= 500)
            {
                var reason = $"Discord returned HTTP {(int)result.StatusCode}.";
                await RetryAsync(alert, result.RetryAfter, reason, cancellationToken);
                return;
            }

            var permanentReason = $"Discord returned HTTP {(int)result.StatusCode}.";
            await alertRepository.MarkFailedAsync(alert.Id, permanentReason, cancellationToken);
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
        var sourceExpiration = alert.OldestSourceUpdatedAtUtc + _maximumSourceAge;
        if (sourceExpiration < nextAttempt)
        {
            nextAttempt = sourceExpiration;
        }

        if (alert.CommenceTimeUtc < nextAttempt)
        {
            nextAttempt = alert.CommenceTimeUtc;
        }

        await alertRepository.ScheduleRetryAsync(alert.Id, nextAttempt, reason, cancellationToken);
        LogRetry(logger, alert.Id, nextAttempt, reason, null);
    }

    private TimeSpan GetExponentialRetryDelay(int priorAttempts)
    {
        var exponent = Math.Min(priorAttempts, 10);
        var seconds = Math.Pow(2, exponent + 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaximumRetryDelay.TotalSeconds));
    }

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        if (channels.AlertOutbox.Reader.TryRead(out _))
        {
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        waitCancellation.CancelAfter(_options.OutboxPollInterval);
        try
        {
            if (await channels.AlertOutbox.Reader.WaitToReadAsync(waitCancellation.Token))
            {
                channels.AlertOutbox.Reader.TryRead(out _);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
        }
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
