using ABOdds.Domain;

namespace ABOdds.Tests.Alerts;

internal static class TestOpportunity
{
    internal static CalculatedEvOpportunity Create(
        decimal expectedValue = 0.074m,
        decimal decimalOdds = 1.9523809524m,
        decimal fairProbability = 0.55m,
        decimal fairDecimalOdds = 1.8181818182m,
        decimal? line = 3.5m,
        string marketKey = MarketKeys.Spread,
        DateTimeOffset? sourceUpdatedAtUtc = null)
    {
        var updatedAt = sourceUpdatedAtUtc ?? new DateTimeOffset(2026, 9, 4, 2, 17, 32, TimeSpan.Zero);

        return new CalculatedEvOpportunity(
            FairValueId: Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OddsSnapshotId: Guid.Parse("00000000-0000-0000-0000-000000000002"),
            EventId: Guid.Parse("00000000-0000-0000-0000-000000000003"),
            MarketId: Guid.Parse("00000000-0000-0000-0000-000000000004"),
            ProviderEventId: "event-1",
            SportKey: "americanfootball_nfl",
            HomeTeam: "Houston Texans",
            AwayTeam: "Indianapolis Colts",
            CommenceTimeUtc: new DateTimeOffset(2026, 9, 6, 17, 0, 0, TimeSpan.Zero),
            MarketKey: marketKey,
            BookmakerKey: "fanduel",
            BookmakerTitle: "FanDuel",
            SelectionKey: "indianapolis colts",
            SelectionDisplayName: "Colts",
            Line: line,
            DecimalOdds: decimalOdds,
            FairProbability: fairProbability,
            FairDecimalOdds: fairDecimalOdds,
            ExpectedValue: expectedValue,
            Sources:
            [
                new FairValueSource(
                    "pinnacle",
                    "Pinnacle",
                    1.826446281m,
                    0.548m,
                    0.625m,
                    updatedAt),
                new FairValueSource(
                    "betonlineag",
                    "BetOnline",
                    1.813008130m,
                    0.552m,
                    0.375m,
                    updatedAt)
            ]);
    }
}
