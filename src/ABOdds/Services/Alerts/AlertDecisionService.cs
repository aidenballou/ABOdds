using ABOdds.Domain;

namespace ABOdds.Services.Alerts;

public static class AlertDecisionService
{
    public static AlertReason? Evaluate(
        decimal expectedValue,
        decimal? lastAlertedExpectedValue,
        bool hasActiveBaseline,
        decimal realertImprovement)
    {
        if (lastAlertedExpectedValue is null)
        {
            return AlertReason.New;
        }

        if (!hasActiveBaseline)
        {
            return AlertReason.Reappeared;
        }

        return expectedValue - lastAlertedExpectedValue >= realertImprovement
            ? AlertReason.Improved
            : null;
    }
}
