using ABOdds.Configuration;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Pipeline;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.Workers;

public sealed partial class OddsPollingWorker(
    IOddsProvider provider,
    OddsIngestionRepository ingestion,
    OddsPipeline pipeline,
    AdaptivePollingSchedule schedule,
    IOptions<OddsApiOptions> oddsApi,
    IOptions<PollingOptions> polling,
    IHostApplicationLifetime lifetime,
    IClock clock,
    ILogger<OddsPollingWorker> logger) : BackgroundService
{
    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!oddsApi.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var sports = oddsApi.Value.Sports;
        var spent = 0;
        int? remaining = null;
        LogPlan(logger, polling.Value.RunOnce ? sports.Count : -1, provider.EstimatedRequestCost,
            polling.Value.MaximumCreditsPerRun);
        try
        {
            // Finish already-paid work before making another paid request, including after restart.
            await pipeline.ProcessPendingAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var sport in sports)
                {
                    if (!schedule.IsDue(sport, clock.UtcNow)) continue;
                    var cost = provider.EstimatedRequestCost;
                    if (polling.Value.MaximumCreditsPerRun is { } maximum && spent + cost > maximum ||
                        remaining is { } quota && quota < cost)
                    {
                        LogBudgetStop(logger, spent, remaining);
                        lifetime.StopApplication();
                        return;
                    }

                    var batch = await provider.GetOddsAsync(sport, stoppingToken);
                    spent += batch.Quota.LastRequestCost ?? cost;
                    remaining = batch.Quota.Remaining;
                    var id = await ingestion.SaveAsync(batch, stoppingToken);
                    await pipeline.ProcessAsync(id, stoppingToken);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        var quoteCount = batch.Events.Sum(game => game.Quotes.Count);
                        LogProcessed(logger, id, sport, quoteCount, spent);
                    }
                    if (remaining is null || batch.Quota.LastRequestCost is null)
                    {
                        throw new InvalidOperationException("Odds API quota headers are missing; further paid requests are stopped.");
                    }
                    schedule.RecordPoll(sport, clock.UtcNow, batch.Events.Select(game => game.CommenceTimeUtc));
                }

                if (polling.Value.RunOnce)
                {
                    lifetime.StopApplication();
                    return;
                }
                await Task.Delay(schedule.GetNextDelay(clock.UtcNow, sports), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            LogStopped(logger, exception);
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Odds polling is disabled")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Odds polling plan: maximum requests {MaximumRequests} (-1 means continuous), estimated credits per request {Cost}, per-run credit cap {Cap}")]
    private static partial void LogPlan(ILogger logger, int maximumRequests, int cost, int? cap);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping before another paid request: credits used this run {Spent}, provider credits remaining {Remaining}")]
    private static partial void LogBudgetStop(ILogger logger, int spent, int? remaining);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processed batch {BatchId} for {Sport}: {Quotes} quotes, credits used this run {Spent}")]
    private static partial void LogProcessed(ILogger logger, Guid batchId, string sport, int quotes, int spent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling stopped after a provider or processing failure; no more paid requests will be made. Fix the cause before restarting")]
    private static partial void LogStopped(ILogger logger, Exception exception);
}
