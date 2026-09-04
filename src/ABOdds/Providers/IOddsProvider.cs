using ABOdds.Domain;

namespace ABOdds.Providers;

public interface IOddsProvider
{
    Task<NormalizedOddsBatch> GetOddsAsync(
        string sportKey,
        CancellationToken cancellationToken = default);
}
