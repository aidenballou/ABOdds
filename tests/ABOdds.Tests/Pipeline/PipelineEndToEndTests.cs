using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Providers;
using ABOdds.Services.Alerts;
using ABOdds.Services.Calculations;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Pipeline;

public sealed class PipelineEndToEndTests
{
    [Fact]
    public void PregameSnapshot_ProducesFairValueEvCandidateAndNewAlert()
    {
        var observedAt = new DateTimeOffset(2026, 9, 3, 22, 15, 0, TimeSpan.Zero);
        var sourceUpdatedAt = observedAt.AddSeconds(-30);
        var sourceEvent = new TheOddsApiEventDto
        {
            Id = "event-1",
            SportKey = "americanfootball_nfl",
            HomeTeam = "Houston Texans",
            AwayTeam = "Indianapolis Colts",
            CommenceTime = observedAt.AddHours(2),
            Bookmakers =
            [
                Book("pinnacle", "Pinnacle", 1.8333m, 2.05m, sourceUpdatedAt),
                Book("betonlineag", "BetOnline", 1.80m, 2.10m, sourceUpdatedAt),
                Book("lowvig", "LowVig", 1.85m, 2.00m, sourceUpdatedAt),
                Book("fanduel", "FanDuel", 2.00m, 1.90m, sourceUpdatedAt)
            ]
        };

        var normalized = new OddsNormalizer().Normalize(
            "americanfootball_nfl",
            observedAt,
            [sourceEvent],
            new ApiQuotaSnapshot(999, 1, 6));
        var normalizedEvent = Assert.Single(normalized.Events);
        var eventId = Guid.NewGuid();
        var marketId = Guid.NewGuid();
        var quotes = normalizedEvent.Quotes.Select(quote => new MarketQuote(
            Guid.NewGuid(),
            eventId,
            marketId,
            normalizedEvent.ProviderEventId,
            normalizedEvent.SportKey,
            normalizedEvent.HomeTeam,
            normalizedEvent.AwayTeam,
            normalizedEvent.CommenceTimeUtc,
            quote.MarketKey,
            quote.Period,
            quote.BookmakerKey,
            quote.BookmakerTitle,
            quote.SelectionKey,
            quote.SelectionDisplayName,
            quote.DecimalOdds,
            quote.Line,
            normalized.ObservedAtUtc,
            quote.SourceUpdatedAtUtc)).ToArray();

        var fairValues = FairValueCalculator.Calculate(
            quotes,
            observedAt,
            TimeSpan.FromSeconds(90),
            new FairValueOptions
            {
                ReferenceBooks =
                [
                    new ReferenceBookOptions { Key = "pinnacle" },
                    new ReferenceBookOptions { Key = "betonlineag" }
                ]
            });
        var persistedFairValues = fairValues.Select(value => new PersistedFairValue(
            Guid.NewGuid(),
            value.MarketId,
            value.SelectionKey,
            value.SelectionDisplayName,
            value.Line,
            value.FairProbability,
            value.FairDecimalOdds,
            value.Sources)).ToArray();
        var opportunities = EvScanner.Scan(
            quotes,
            persistedFairValues,
            observedAt,
            TimeSpan.FromSeconds(90),
            new EvOptions
            {
                TargetBooks = [new TargetBookOptions { Key = "fanduel" }]
            });

        var opportunity = Assert.Single(opportunities);
        Assert.Equal("indianapolis colts", opportunity.SelectionKey);
        Assert.Equal("fanduel", opportunity.BookmakerKey);
        Assert.InRange(opportunity.ExpectedValue, 0.05m, 0.07m);

        var decisionService = new AlertDecisionService(Options.Create(new EvOptions()));
        var decision = decisionService.Evaluate(opportunity, null, observedAt);
        var discordMessage = DiscordAlertFormatter.Format(opportunity, observedAt);

        Assert.Equal(AlertReason.New, decision.Reason);
        Assert.Contains("Indianapolis Colts", discordMessage, StringComparison.Ordinal);
        Assert.Contains("FanDuel", discordMessage, StringComparison.Ordinal);
        Assert.Contains("BetOnline confirmed", discordMessage, StringComparison.Ordinal);
    }

    private static TheOddsApiBookmakerDto Book(
        string key,
        string title,
        decimal coltsOdds,
        decimal texansOdds,
        DateTimeOffset sourceUpdatedAt) => new()
        {
            Key = key,
            Title = title,
            LastUpdate = sourceUpdatedAt,
            Markets =
            [
                new TheOddsApiMarketDto
                {
                    Key = MarketKeys.Moneyline,
                    LastUpdate = sourceUpdatedAt,
                    Outcomes =
                    [
                        new TheOddsApiOutcomeDto { Name = "Indianapolis Colts", Price = coltsOdds },
                        new TheOddsApiOutcomeDto { Name = "Houston Texans", Price = texansOdds }
                    ]
                }
            ]
        };
}
