using ABOdds.Domain;
using ABOdds.Services.Alerts;

namespace ABOdds.Tests.Alerts;

public sealed class AlertDecisionServiceTests
{
    public static TheoryData<decimal, decimal?, bool, decimal, AlertReason?> Decisions => new()
    {
        { 0.04m, null, false, 0.01m, AlertReason.New },
        { 0.035m, 0.06m, false, 0.01m, AlertReason.Reappeared },
        { 0.03m, 0.04m, true, 0.01m, null },
        { 0.04m, 0.04m, true, 0.01m, null },
        { 0.049m, 0.04m, true, 0.01m, null },
        { 0.05m, 0.04m, true, 0.01m, AlertReason.Improved },
        { 0.06m, 0.04m, true, 0.01m, AlertReason.Improved },
        { 0.05m, 0.04m, true, 0.02m, null }
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Evaluate_UsesTheLastAlertBaselineAndConfiguredImprovement(
        decimal expectedValue, decimal? baseline, bool active, decimal improvement, AlertReason? expected)
    {
        Assert.Equal(expected, AlertDecisionService.Evaluate(expectedValue, baseline, active, improvement));
    }
}
