using ABOdds.Configuration;

namespace ABOdds.Services;

public sealed class AdaptivePollingSchedule
{
    private readonly PollingOptions _options;
    private readonly Dictionary<string, DateTimeOffset> _nextPollAt = new(StringComparer.Ordinal);

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

    public bool IsDue(string sport, DateTimeOffset now) =>
        !_nextPollAt.TryGetValue(sport, out var due) || due <= now;

    public void RecordPoll(string sport, DateTimeOffset now, IEnumerable<DateTimeOffset> commenceTimes)
    {
        var times = commenceTimes.ToArray();
        var next = now + GetDelay(now, times);
        foreach (var kickoff in times)
        {
            var entersNearWindow = kickoff - _options.NearEventWindow;
            if (entersNearWindow > now && entersNearWindow < next) next = entersNearWindow;
        }
        _nextPollAt[sport] = next;
    }

    public TimeSpan GetNextDelay(DateTimeOffset now, IEnumerable<string> sports)
    {
        var next = sports.Min(sport => _nextPollAt.GetValueOrDefault(sport, now));
        return next > now ? next - now : TimeSpan.Zero;
    }
}
