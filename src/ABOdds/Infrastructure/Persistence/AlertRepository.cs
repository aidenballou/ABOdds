using System.Text.Json;
using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ABOdds.Infrastructure.Persistence;

public sealed record PendingAlert(
    Guid Id,
    string Message,
    int DeliveryAttempts,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset CommenceTimeUtc,
    DateTimeOffset OldestSourceUpdatedAtUtc,
    bool StateIsActive,
    DateTimeOffset StateLastSeenAtUtc);

public sealed class AlertRepository(
    IDbContextFactory<BettingDbContext> contextFactory,
    IOptions<EvOptions> evOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Guid>> ApplyRulesAsync(
        AlertBatchData alertBatch,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dbContext.PollBatches.SingleAsync(
            value => value.Id == alertBatch.BatchId,
            cancellationToken);
        if (batch.AlertRulesCompletedAtUtc is not null)
        {
            return [];
        }

        var newerBatchWasApplied = await dbContext.PollBatches
            .AsNoTracking()
            .AnyAsync(value =>
                value.SportKey == batch.SportKey &&
                value.ObservedAtUtc > batch.ObservedAtUtc &&
                value.AlertRulesCompletedAtUtc != null,
                cancellationToken);
        if (newerBatchWasApplied)
        {
            batch.AlertRulesCompletedAtUtc = alertBatch.ProcessedAtUtc;
            batch.LastFailedStage = null;
            batch.LastError = null;
            batch.LastFailedAtUtc = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            return [];
        }

        var currentOpportunities = alertBatch.Opportunities
            .GroupBy(value => value.Opportunity.BetKey)
            .Select(group => group.OrderByDescending(value => value.Opportunity.ExpectedValue).First())
            .ToArray();
        var currentMarketIds = currentOpportunities
            .Select(value => value.Opportunity.MarketId)
            .Distinct()
            .ToArray();
        var states = await dbContext.AlertStates
            .Where(value =>
                value.Event.SportKey == alertBatch.SportKey &&
                (value.IsActive || currentMarketIds.Contains(value.MarketId)))
            .ToListAsync(cancellationToken);
        var statesByKey = states.ToDictionary(ToBetKey);
        var currentKeys = currentOpportunities
            .Select(value => value.Opportunity.BetKey)
            .ToHashSet();

        foreach (var state in states.Where(value => value.IsActive && !currentKeys.Contains(ToBetKey(value))))
        {
            state.IsActive = false;
            state.LastDisappearedAtUtc = alertBatch.ObservedAtUtc;
        }

        var createdAlertIds = new List<Guid>();
        foreach (var current in currentOpportunities)
        {
            var opportunity = current.Opportunity;
            var key = opportunity.BetKey;
            statesByKey.TryGetValue(key, out var state);
            // A baseline from an earlier appearance does not cover this one.
            var hasActiveBaseline = state is { IsActive: true } &&
                (state.LastDisappearedAtUtc is null || state.LastAlertedAtUtc > state.LastDisappearedAtUtc);
            var reason = AlertDecisionService.Evaluate(opportunity.ExpectedValue,
                state?.LastAlertedExpectedValue, hasActiveBaseline, evOptions.Value.RealertImprovement);

            if (state is null)
            {
                state = new AlertStateEntity
                {
                    Id = Guid.NewGuid(),
                    EventId = key.EventId,
                    MarketId = key.MarketId,
                    SelectionKey = key.SelectionKey,
                    LineKey = key.LineKey,
                    BookmakerKey = key.BookmakerKey
                };
                dbContext.AlertStates.Add(state);
                statesByKey.Add(key, state);
            }

            state.IsActive = true;
            state.LastSeenAtUtc = alertBatch.ObservedAtUtc;
            state.LastSeenExpectedValue = opportunity.ExpectedValue;
            state.LastSeenDecimalOdds = opportunity.DecimalOdds;

            if (reason is null)
            {
                continue;
            }

            state.LastAlertedAtUtc = alertBatch.ObservedAtUtc;
            state.LastAlertedExpectedValue = opportunity.ExpectedValue;
            state.LastAlertedDecimalOdds = opportunity.DecimalOdds;
            var alertId = Guid.NewGuid();
            createdAlertIds.Add(alertId);
            dbContext.Alerts.Add(new AlertEntity
            {
                Id = alertId,
                AlertStateId = state.Id,
                EvOpportunityId = current.OpportunityId,
                Reason = reason.Value,
                Message = DiscordAlertFormatter.Format(opportunity, alertBatch.ObservedAtUtc),
                DeliveryStatus = AlertDeliveryStatus.Pending,
                NextAttemptAtUtc = alertBatch.ProcessedAtUtc,
                CreatedAtUtc = alertBatch.ProcessedAtUtc
            });
        }

        batch.AlertRulesCompletedAtUtc = alertBatch.ProcessedAtUtc;
        batch.LastFailedStage = null;
        batch.LastError = null;
        batch.LastFailedAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return createdAlertIds;
    }

    public async Task<PendingAlert?> GetNextPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        // V1 has one webhook. Its cooldown also covers new alerts and process restarts.
        if (await dbContext.Alerts.AnyAsync(value => value.RateLimitedUntilUtc > now, cancellationToken))
        {
            return null;
        }
        var pending = await dbContext.Alerts
            .AsNoTracking()
            .Where(value =>
                value.DeliveryStatus == AlertDeliveryStatus.Pending &&
                value.NextAttemptAtUtc <= now)
            .OrderBy(value => value.NextAttemptAtUtc)
            .ThenBy(value => value.CreatedAtUtc)
            .Select(value => new
            {
                value.Id,
                value.Message,
                value.DeliveryAttempts,
                value.EvOpportunity.OddsSnapshot.ObservedAtUtc,
                value.EvOpportunity.OddsSnapshot.Market.Event.CommenceTimeUtc,
                TargetSourceUpdatedAtUtc = value.EvOpportunity.OddsSnapshot.SourceUpdatedAtUtc,
                value.EvOpportunity.FairValue.SourcesJson,
                StateIsActive = value.AlertState.IsActive,
                StateLastSeenAtUtc = value.AlertState.LastSeenAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (pending is null)
        {
            return null;
        }

        var referenceSources = JsonSerializer.Deserialize<IReadOnlyList<FairValueSource>>(
            pending.SourcesJson,
            JsonOptions) ?? [];
        var oldestSourceUpdatedAt = referenceSources
            .Select(value => value.SourceUpdatedAtUtc)
            .Append(pending.TargetSourceUpdatedAtUtc)
            .Min();
        return new PendingAlert(
            pending.Id,
            pending.Message,
            pending.DeliveryAttempts,
            pending.ObservedAtUtc,
            pending.CommenceTimeUtc,
            oldestSourceUpdatedAt,
            pending.StateIsActive,
            pending.StateLastSeenAtUtc);
    }

    public async Task MarkSentAsync(
        Guid alertId,
        DateTimeOffset sentAtUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? cooldownUntilUtc = null)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await dbContext.Alerts.SingleAsync(value => value.Id == alertId, cancellationToken);
        alert.DeliveryStatus = AlertDeliveryStatus.Sent;
        alert.DeliveryAttempts++;
        alert.SentAtUtc = sentAtUtc;
        alert.LastDeliveryError = null;
        alert.RateLimitedUntilUtc = cooldownUntilUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(
        Guid alertId,
        DateTimeOffset nextAttemptAtUtc,
        string error,
        CancellationToken cancellationToken,
        bool rateLimited = false)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await dbContext.Alerts.SingleAsync(value => value.Id == alertId, cancellationToken);
        alert.DeliveryAttempts++;
        alert.NextAttemptAtUtc = nextAttemptAtUtc;
        if (rateLimited) alert.RateLimitedUntilUtc = nextAttemptAtUtc;
        alert.LastDeliveryError = Truncate(error);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid alertId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await dbContext.Alerts
            .Include(value => value.AlertState)
            .Include(value => value.EvOpportunity).ThenInclude(value => value.OddsSnapshot)
            .SingleAsync(value => value.Id == alertId, cancellationToken);
        alert.DeliveryStatus = AlertDeliveryStatus.Failed;
        alert.DeliveryAttempts++;
        alert.LastDeliveryError = Truncate(error);
        await RestoreLastDeliveredBaselineIfLatestAsync(dbContext, alert, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkExpiredAsync(
        Guid alertId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var alert = await dbContext.Alerts
            .Include(value => value.AlertState)
            .Include(value => value.EvOpportunity).ThenInclude(value => value.OddsSnapshot)
            .SingleAsync(value => value.Id == alertId, cancellationToken);
        alert.DeliveryStatus = AlertDeliveryStatus.Expired;
        alert.LastDeliveryError = Truncate(reason);
        await RestoreLastDeliveredBaselineIfLatestAsync(dbContext, alert, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static BetKey ToBetKey(AlertStateEntity state) => new(
        state.EventId,
        state.MarketId,
        state.SelectionKey,
        state.LineKey,
        state.BookmakerKey);

    private static async Task RestoreLastDeliveredBaselineIfLatestAsync(
        BettingDbContext dbContext,
        AlertEntity alert,
        CancellationToken cancellationToken)
    {
        var state = alert.AlertState;
        if (state.LastAlertedAtUtc != alert.EvOpportunity.OddsSnapshot.ObservedAtUtc)
        {
            return;
        }

        var lastDelivered = await dbContext.Alerts
            .AsNoTracking()
            .Where(value =>
                value.AlertStateId == state.Id &&
                value.DeliveryStatus == AlertDeliveryStatus.Sent)
            .OrderByDescending(value => value.SentAtUtc)
            .ThenByDescending(value => value.CreatedAtUtc)
            .Select(value => new
            {
                value.EvOpportunity.OddsSnapshot.ObservedAtUtc,
                value.EvOpportunity.ExpectedValue,
                value.EvOpportunity.OddsSnapshot.DecimalOdds
            })
            .FirstOrDefaultAsync(cancellationToken);

        state.LastAlertedAtUtc = lastDelivered?.ObservedAtUtc;
        state.LastAlertedExpectedValue = lastDelivered?.ExpectedValue;
        state.LastAlertedDecimalOdds = lastDelivered?.DecimalOdds;
    }

    private static string Truncate(string value) => value[..Math.Min(value.Length, 2000)];
}
