using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Alerts;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Alerts;

public sealed class AlertDecisionServiceTests
{
    private static readonly DateTimeOffset FirstSeen =
        new(2026, 9, 4, 2, 17, 32, TimeSpan.Zero);

    private readonly AlertDecisionService _service = new(
        Options.Create(new EvOptions { RealertImprovement = 0.01m }));

    [Fact]
    public void Evaluate_NewOpportunity_AlertsAndBuildsStructuredBetKey()
    {
        var opportunity = TestOpportunity.Create(expectedValue: 0.04m);

        var decision = _service.Evaluate(opportunity, previousState: null, FirstSeen);

        Assert.True(decision.ShouldAlert);
        Assert.Equal(AlertReason.New, decision.Reason);
        Assert.Equal(
            new BetKey(
                opportunity.EventId,
                opportunity.MarketId,
                opportunity.SelectionKey,
                "3.5",
                opportunity.BookmakerKey),
            decision.State.BetKey);
        Assert.True(decision.State.IsActive);
        Assert.Equal(FirstSeen, decision.State.LastSeenAtUtc);
        Assert.Equal(0.04m, decision.State.LastSeenExpectedValue);
        Assert.Equal(0.04m, decision.State.LastAlertedExpectedValue);
    }

    [Fact]
    public void Evaluate_ActiveOpportunityBelowImprovement_DoesNotAlertButUpdatesLastSeen()
    {
        var initial = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.04m),
            previousState: null,
            FirstSeen);
        var seenAgainAt = FirstSeen.AddSeconds(30);

        var decision = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.049m),
            initial.State,
            seenAgainAt);

        Assert.False(decision.ShouldAlert);
        Assert.Null(decision.Reason);
        Assert.True(decision.State.IsActive);
        Assert.Equal(seenAgainAt, decision.State.LastSeenAtUtc);
        Assert.Equal(0.049m, decision.State.LastSeenExpectedValue);
        Assert.Equal(0.04m, decision.State.LastAlertedExpectedValue);
    }

    [Fact]
    public void Evaluate_ActiveOpportunityImprovesByOnePercentagePoint_Alerts()
    {
        var initial = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.04m),
            previousState: null,
            FirstSeen);
        var seenWithoutAlert = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.049m),
            initial.State,
            FirstSeen.AddSeconds(30));

        var decision = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.05m),
            seenWithoutAlert.State,
            FirstSeen.AddMinutes(1));

        Assert.True(decision.ShouldAlert);
        Assert.Equal(AlertReason.Improved, decision.Reason);
        Assert.Equal(0.05m, decision.State.LastAlertedExpectedValue);
    }

    [Fact]
    public void Evaluate_InactiveOpportunityReappears_AlertsRegardlessOfEvChange()
    {
        var initial = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.06m),
            previousState: null,
            FirstSeen);
        var inactiveState = AlertDecisionService.MarkInactive(initial.State);
        var reappearedAt = FirstSeen.AddMinutes(2);

        var decision = _service.Evaluate(
            TestOpportunity.Create(expectedValue: 0.035m),
            inactiveState,
            reappearedAt);

        Assert.True(decision.ShouldAlert);
        Assert.Equal(AlertReason.Reappeared, decision.Reason);
        Assert.True(decision.State.IsActive);
        Assert.Equal(reappearedAt, decision.State.LastSeenAtUtc);
        Assert.Equal(0.035m, decision.State.LastAlertedExpectedValue);
    }

    [Fact]
    public void Evaluate_StateForDifferentBet_Throws()
    {
        var opportunity = TestOpportunity.Create();
        var otherState = new AlertTrackingState(
            new BetKey(Guid.NewGuid(), opportunity.MarketId, opportunity.SelectionKey, opportunity.LineKey, opportunity.BookmakerKey),
            IsActive: true,
            FirstSeen,
            opportunity.ExpectedValue,
            opportunity.ExpectedValue);

        var exception = Assert.Throws<ArgumentException>(() =>
            _service.Evaluate(opportunity, otherState, FirstSeen));

        Assert.Equal("previousState", exception.ParamName);
    }
}
