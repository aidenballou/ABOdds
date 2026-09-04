using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Services.Calculations;

namespace ABOdds.Tests.Calculations;

public sealed class EvScannerTests
{
    private static readonly TimeSpan MaximumSourceAge = TimeSpan.FromSeconds(90);

    [Fact]
    public void Scan_ReturnsOnlyTargetBooksAtOrAboveTheEvThreshold()
    {
        var fairValue = CalculationTestData.FairValue(0.55m);
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "draftkings", "Team A", 1.85m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "not-a-target", "Team A", 3m)
        };

        var result = EvScanner.Scan(
            quotes,
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel", "draftkings"));

        var opportunity = Assert.Single(result);
        Assert.Equal("fanduel", opportunity.BookmakerKey);
        Assert.Equal(fairValue.Id, opportunity.FairValueId);
        Assert.Equal(0.10m, opportunity.ExpectedValue);
    }

    [Fact]
    public void Scan_IncludesAnOpportunityExactlyAtTheEvThreshold()
    {
        var fairValue = CalculationTestData.FairValue(0.5m);
        var quote = CalculationTestData.Quote(
            MarketKeys.Moneyline,
            "fanduel",
            "Team A",
            2.06m);

        var result = EvScanner.Scan(
            [quote],
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel"));

        var opportunity = Assert.Single(result);
        Assert.Equal(0.03m, opportunity.ExpectedValue);
    }

    [Fact]
    public void Scan_RequiresAnExactSpreadLineMatch()
    {
        var fairValue = CalculationTestData.FairValue(0.55m, line: 3.5m);
        var quote = CalculationTestData.Quote(
            MarketKeys.Spread,
            "fanduel",
            "Team A",
            2m,
            4m);

        var result = EvScanner.Scan(
            [quote],
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel"));

        Assert.Empty(result);
    }

    [Fact]
    public void Scan_ExcludesAStaleTargetQuote()
    {
        var fairValue = CalculationTestData.FairValue(0.55m);
        var quote = CalculationTestData.Quote(
            MarketKeys.Moneyline,
            "fanduel",
            "Team A",
            2m,
            sourceUpdatedAtUtc: CalculationTestData.Now - MaximumSourceAge - TimeSpan.FromSeconds(1));

        var result = EvScanner.Scan(
            [quote],
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel"));

        Assert.Empty(result);
    }

    [Fact]
    public void Scan_ExcludesAFairValueWhoseSourcesHaveGoneStale()
    {
        var fairValue = CalculationTestData.FairValue(
            0.55m,
            sourceUpdatedAtUtc: CalculationTestData.Now - MaximumSourceAge - TimeSpan.FromSeconds(1));
        var quote = CalculationTestData.Quote(
            MarketKeys.Moneyline,
            "fanduel",
            "Team A",
            2m);

        var result = EvScanner.Scan(
            [quote],
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel"));

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("live", 1)]
    [InlineData(MarketPeriods.Pregame, -1)]
    public void Scan_ExcludesNonPregameOrStartedTargets(string period, int commenceOffsetMinutes)
    {
        var fairValue = CalculationTestData.FairValue(0.55m);
        var quote = CalculationTestData.Quote(
            MarketKeys.Moneyline,
            "fanduel",
            "Team A",
            2m,
            period: period,
            commenceTimeUtc: CalculationTestData.Now.AddMinutes(commenceOffsetMinutes));

        var result = EvScanner.Scan(
            [quote],
            [fairValue],
            CalculationTestData.Now,
            MaximumSourceAge,
            Options(0.03m, "fanduel"));

        Assert.Empty(result);
    }

    private static EvOptions Options(decimal minimumExpectedValue, params string[] books) =>
        new()
        {
            MinimumExpectedValue = minimumExpectedValue,
            TargetBooks = books
                .Select(book => new TargetBookOptions { Key = book, DisplayName = book })
                .ToList()
        };
}
