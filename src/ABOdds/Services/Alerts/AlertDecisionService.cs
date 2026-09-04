using ABOdds.Configuration;
using ABOdds.Domain;
using Microsoft.Extensions.Options;

namespace ABOdds.Services.Alerts;

public sealed record AlertTrackingState(
    BetKey BetKey,
    bool IsActive,
    DateTimeOffset LastSeenAtUtc,
    decimal LastSeenExpectedValue,
    decimal LastAlertedExpectedValue);

public sealed record AlertDecision(AlertReason? Reason, AlertTrackingState State)
{
    public bool ShouldAlert => Reason.HasValue;
}

public sealed class AlertDecisionService
{
    private readonly decimal _realertImprovement;

    public AlertDecisionService(IOptions<EvOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _realertImprovement = options.Value.RealertImprovement;
        if (_realertImprovement < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _realertImprovement,
                "The re-alert improvement must not be negative.");
        }
    }

    public AlertDecision Evaluate(
        CalculatedEvOpportunity opportunity,
        AlertTrackingState? previousState,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var betKey = BetKeyFactory.Create(opportunity);
        if (previousState is not null && previousState.BetKey != betKey)
        {
            throw new ArgumentException(
                "The previous alert state belongs to a different bet.",
                nameof(previousState));
        }

        var reason = GetReason(opportunity.ExpectedValue, previousState);
        var lastAlertedExpectedValue = reason.HasValue
            ? opportunity.ExpectedValue
            : previousState!.LastAlertedExpectedValue;

        var nextState = new AlertTrackingState(
            betKey,
            IsActive: true,
            observedAtUtc,
            opportunity.ExpectedValue,
            lastAlertedExpectedValue);

        return new AlertDecision(reason, nextState);
    }

    public static AlertTrackingState MarkInactive(AlertTrackingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { IsActive = false };
    }

    private AlertReason? GetReason(decimal expectedValue, AlertTrackingState? previousState)
    {
        if (previousState is null)
        {
            return AlertReason.New;
        }

        if (!previousState.IsActive)
        {
            return AlertReason.Reappeared;
        }

        return expectedValue - previousState.LastAlertedExpectedValue >= _realertImprovement
            ? AlertReason.Improved
            : null;
    }
}

public static class BetKeyFactory
{
    public static BetKey Create(CalculatedEvOpportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        return new BetKey(
            opportunity.EventId,
            opportunity.MarketId,
            opportunity.SelectionKey,
            opportunity.LineKey,
            opportunity.BookmakerKey);
    }
}
