using ABOdds.Domain;

namespace ABOdds.Providers;

public interface IOddsProvider
{
    int EstimatedRequestCost { get; }
    Task<NormalizedOddsBatch> GetOddsAsync(
        string sportKey,
        CancellationToken cancellationToken = default);
}
