using ABOdds.Configuration;

namespace ABOdds.Services;

public sealed class AdaptivePollingSchedule
{
    private readonly PollingOptions _options;

    public AdaptivePollingSchedule(PollingOptions options)
    {
        _options = options;
    }

    public TimeSpan GetDelay(DateTimeOffset now, IEnumerable<DateTimeOffset> commenceTimes)
    {
        var hasNearEvent = commenceTimes.Any(commenceTime =>
            commenceTime > now && commenceTime - now <= _options.NearEventWindow);

        return hasNearEvent ? _options.NearEventInterval : _options.NormalInterval;
    }
}
