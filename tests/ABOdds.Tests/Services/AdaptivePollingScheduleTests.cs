using ABOdds.Configuration;
using ABOdds.Services;

namespace ABOdds.Tests.Services;

public sealed class AdaptivePollingScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);
    private readonly AdaptivePollingSchedule _schedule = new(new PollingOptions());

    [Theory]
    [InlineData(1)]
    [InlineData(360)]
    public void GetDelay_UsesOneMinuteInsideSixHourWindow(int minutesUntilKickoff)
    {
        var result = _schedule.GetDelay(Now, [Now.AddMinutes(minutesUntilKickoff)]);

        Assert.Equal(TimeSpan.FromMinutes(1), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(361)]
    public void GetDelay_UsesFiveMinutesOutsideSixHourWindow(int minutesUntilKickoff)
    {
        var result = _schedule.GetDelay(Now, [Now.AddMinutes(minutesUntilKickoff)]);

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void GetDelay_UsesFiveMinutesWhenThereAreNoEvents()
    {
        var result = _schedule.GetDelay(Now, []);

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }
}
