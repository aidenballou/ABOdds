using ABOdds.Configuration;
using ABOdds.Services;

namespace ABOdds.Tests.Services;

public sealed class AdaptivePollingScheduleTests
{
    [Fact]
    public void RecordPoll_WakesWhenEventEntersNearWindow()
    {
        var schedule = new AdaptivePollingSchedule(new PollingOptions());
        schedule.RecordPoll("nfl", Now, [Now.AddHours(6).AddSeconds(30)]);
        Assert.Equal(TimeSpan.FromSeconds(30), schedule.GetNextDelay(Now, ["nfl"]));
        Assert.True(schedule.IsDue("nfl", Now.AddSeconds(30)));
    }

    [Fact]
    public void DueTimes_AreIndependentForEachSport()
    {
        var schedule = new AdaptivePollingSchedule(new PollingOptions());
        schedule.RecordPoll("nfl", Now, [Now.AddHours(2)]);
        schedule.RecordPoll("ncaaf", Now, [Now.AddDays(2)]);
        Assert.False(schedule.IsDue("nfl", Now.AddSeconds(59)));
        Assert.True(schedule.IsDue("nfl", Now.AddMinutes(1)));
        Assert.False(schedule.IsDue("ncaaf", Now.AddMinutes(1)));
        Assert.True(schedule.IsDue("ncaaf", Now.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), schedule.GetNextDelay(Now.AddSeconds(30), ["nfl", "ncaaf"]));
        schedule.RecordPoll("nfl", Now.AddHours(3), []);
        Assert.False(schedule.IsDue("nfl", Now.AddHours(3).AddMinutes(1)));
    }

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
