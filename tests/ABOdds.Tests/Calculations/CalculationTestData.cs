using ABOdds.Domain;

namespace ABOdds.Tests.Calculations;

internal static class CalculationTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 3, 22, 15, 0, TimeSpan.Zero);
    internal static readonly Guid EventId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid MarketId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    internal static MarketQuote Quote(
        string marketKey,
        string bookmakerKey,
        string selection,
        decimal decimalOdds,
        decimal? line = null,
        DateTimeOffset? sourceUpdatedAtUtc = null,
        DateTimeOffset? commenceTimeUtc = null,
        string period = MarketPeriods.Pregame,
        Guid? marketId = null) =>
        new(
            Guid.NewGuid(),
            EventId,
            marketId ?? MarketId,
            "event-1",
            "americanfootball_nfl",
            "Team A",
            "Team B",
            commenceTimeUtc ?? Now.AddHours(2),
            marketKey,
            period,
            bookmakerKey,
            bookmakerKey,
            OddsKey.NormalizeSelection(selection),
            selection,
            decimalOdds,
            line,
            Now,
            sourceUpdatedAtUtc ?? Now.AddSeconds(-10));

    internal static PersistedFairValue FairValue(
        decimal fairProbability,
        string selection = "Team A",
        decimal? line = null,
        Guid? marketId = null,
        DateTimeOffset? sourceUpdatedAtUtc = null) =>
        new(
            Guid.NewGuid(),
            marketId ?? MarketId,
            OddsKey.NormalizeSelection(selection),
            selection,
            line,
            fairProbability,
            1m / fairProbability,
            [
                new FairValueSource(
                    "pinnacle",
                    "Pinnacle",
                    1.9m,
                    fairProbability,
                    1m,
                    sourceUpdatedAtUtc ?? Now.AddSeconds(-10)) { Role = FairValueSourceRole.Primary }
            ]);
}
