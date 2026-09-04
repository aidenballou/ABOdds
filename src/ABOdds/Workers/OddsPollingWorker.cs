using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed class OddsPollingWorker(
    IOddsProvider oddsProvider,
    OddsIngestionRepository ingestionRepository,
    PipelineChannels channels,
    AdaptivePollingSchedule pollingSchedule,
    IOptions<OddsApiOptions> oddsApiOptions,
    IClock clock,
    ILogger<OddsPollingWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDisabled = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2001, nameof(LogDisabled)),
        "Odds polling is disabled; set OddsApi:Enabled to true to ingest live data");

    private static readonly Action<ILogger, string, Exception?> LogPollFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2002, nameof(LogPollFailure)),
            "Odds poll failed for {SportKey}");

    private static readonly Action<ILogger, int, string, int, int, Exception?> LogPollComplete =
        LoggerMessage.Define<int, string, int, int>(
            LogLevel.Information,
            new EventId(2003, nameof(LogPollComplete)),
            "Stored {EventCount} pregame events for {SportKey}; quota remaining {QuotaRemaining}, request cost {RequestCost}");

    private static readonly Action<ILogger, double, Exception?> LogNextPoll =
        LoggerMessage.Define<double>(
            LogLevel.Information,
            new EventId(2004, nameof(LogNextPoll)),
            "Next odds poll in {DelaySeconds} seconds");

    private readonly OddsApiOptions _options = oddsApiOptions.Value;
    private readonly Dictionary<string, IReadOnlyList<DateTimeOffset>> _knownCommenceTimes =
        new(StringComparer.Ordinal);

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
            foreach (var sportKey in _options.Sports)
            {
                try
                {
                    var batch = await oddsProvider.GetOddsAsync(sportKey, stoppingToken);
                    var batchId = await ingestionRepository.SaveAsync(batch, stoppingToken);
                    _knownCommenceTimes[sportKey] = batch.Events
                        .Select(value => value.CommenceTimeUtc)
                        .ToArray();
                    await channels.FairValueBatches.Writer.WriteAsync(batchId, stoppingToken);
                    LogPollComplete(
                        logger,
                        batch.Events.Count,
                        sportKey,
                        batch.Quota.Remaining ?? -1,
                        batch.Quota.LastRequestCost ?? -1,
                        null);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogPollFailure(logger, sportKey, exception);
                }
            }

            var now = clock.UtcNow;
            var nextDelay = pollingSchedule.GetDelay(now, _knownCommenceTimes.Values.SelectMany(value => value));
            LogNextPoll(logger, nextDelay.TotalSeconds, null);
            await Task.Delay(nextDelay, stoppingToken);
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
