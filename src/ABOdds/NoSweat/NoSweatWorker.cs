using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Services.Alerts;
using ABOdds.Time;
using Microsoft.Extensions.Options;

namespace ABOdds.NoSweat;

public sealed partial class NoSweatWorker(
    IOddsProvider provider, IDiscordWebhookClient discord,
    IOptions<NoSweatOptions> settings, IOptions<OddsApiOptions> odds,
    IOptions<PollingOptions> polling, IOptions<DiscordOptions> notifications,
    AdaptivePollingSchedule schedule, IClock clock, IHostApplicationLifetime lifetime,
    ILogger<NoSweatWorker> logger) : BackgroundService
{
    public int ExitCode { get; private set; }
    private readonly Dictionary<string, NormalizedOddsBatch> _snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sentPages = new(StringComparer.Ordinal);
    private DateTimeOffset _discordAvailableAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!odds.Value.Enabled) { LogDisabled(logger); return; }
        var spent = 0;
        int? remaining = null;
        LogPlan(logger, settings.Value.Stage, provider.EstimatedRequestCost, polling.Value.MaximumCreditsPerRun);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var stopAfterReport = false;
                foreach (var sport in odds.Value.Sports)
                {
                    if (!schedule.IsDue(sport, clock.UtcNow)) continue;
                    var cost = provider.EstimatedRequestCost;
                    if (polling.Value.MaximumCreditsPerRun is { } cap && spent + cost > cap ||
                        remaining is { } quota && quota < cost)
                    {
                        LogBudgetStop(logger, spent, remaining);
                        stopAfterReport = true;
                        break;
                    }
                    var batch = await provider.GetOddsAsync(sport, stoppingToken);
                    spent += batch.Quota.LastRequestCost ?? cost;
                    remaining = batch.Quota.Remaining;
                    _snapshots[sport] = batch;
                    schedule.RecordPoll(sport, clock.UtcNow, batch.Events.Select(game => game.CommenceTimeUtc));
                    if (remaining is null || batch.Quota.LastRequestCost is null)
                    {
                        ExitCode = 1;
                        LogMissingQuota(logger);
                        stopAfterReport = true;
                        break;
                    }
                }

                var markets = HedgeMarketMatcher.Match(_snapshots.Values, settings.Value, clock.UtcNow, polling.Value.MaximumSourceAge);
                var report = settings.Value.Stage == NoSweatStage.Qualifying
                    ? NoSweatFormatter.Qualifying(NoSweatOptimizer.FindQualifying(markets, settings.Value), polling.Value.MaximumSourceAge)
                    : NoSweatFormatter.Bonus(NoSweatOptimizer.FindConversions(markets, settings.Value.BonusBetAmount,
                        settings.Value.Bankroll), polling.Value.MaximumSourceAge);
                foreach (var page in report.Pages) LogReport(logger, page);
                if (notifications.Value.Enabled) await PublishAsync(report, stoppingToken);
                if (polling.Value.RunOnce || stopAfterReport) { lifetime.StopApplication(); return; }
                await Task.Delay(schedule.GetNextDelay(clock.UtcNow, odds.Value.Sports), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ExitCode = 1;
            LogStopped(logger, exception);
            lifetime.StopApplication();
        }
    }

    private async Task PublishAsync(NoSweatReport report, CancellationToken cancellationToken)
    {
        _sentPages.IntersectWith(report.Pages);
        foreach (var page in report.Pages.Where(page => !_sentPages.Contains(page)))
        {
            if (clock.UtcNow >= report.ValidUntilUtc || _discordAvailableAt >= report.ValidUntilUtc) return;
            var delay = _discordAvailableAt - clock.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            if (clock.UtcNow >= report.ValidUntilUtc) return;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var validity = report.ValidUntilUtc - clock.UtcNow;
            attempt.CancelAfter(validity < notifications.Value.RequestTimeout ? validity : notifications.Value.RequestTimeout);
            try
            {
                var result = await discord.SendAsync(page + $"\n-# Observed <t:{clock.UtcNow.ToUnixTimeSeconds()}:R>; check both prices before placing bets.", attempt.Token);
                if (result.IsSuccess)
                {
                    _sentPages.Add(page);
                    if (result.Cooldown is { } cooldown)
                        _discordAvailableAt = clock.UtcNow + cooldown + TimeSpan.FromMilliseconds(250);
                    continue;
                }
                if (result.IsRateLimited)
                {
                    var retry = result.RetryAfter ?? TimeSpan.FromSeconds(5);
                    if (result.Cooldown > retry) retry = result.Cooldown.Value;
                    _discordAvailableAt = clock.UtcNow + retry + TimeSpan.FromMilliseconds(250);
                }
                LogDeliveryFailure(logger, $"HTTP {(int)result.StatusCode}; a fresh report will retry on the next poll");
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LogDeliveryFailure(logger, "request failed; a fresh report will retry on the next poll");
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No-sweat mode selected, but Odds API polling is disabled; EV workers are not registered")]
    private static partial void LogDisabled(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "No-sweat {Stage} polling: estimated credits per request {Cost}, per-run cap {Cap}")]
    private static partial void LogPlan(ILogger logger, NoSweatStage stage, int cost, int? cap);
    [LoggerMessage(Level = LogLevel.Information, Message = "No-sweat report:\n{Report}")]
    private static partial void LogReport(ILogger logger, string report);
    [LoggerMessage(Level = LogLevel.Information, Message = "No-sweat polling stopped at credit limit: used {Spent}, provider remaining {Remaining}")]
    private static partial void LogBudgetStop(ILogger logger, int spent, int? remaining);
    [LoggerMessage(Level = LogLevel.Error, Message = "Odds API quota headers are missing; reporting the current snapshot and stopping further requests")]
    private static partial void LogMissingQuota(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "No-sweat Discord delivery: {Reason}")]
    private static partial void LogDeliveryFailure(ILogger logger, string reason);
    [LoggerMessage(Level = LogLevel.Error, Message = "No-sweat polling stopped after an error; no further paid requests will be made")]
    private static partial void LogStopped(ILogger logger, Exception exception);
}
