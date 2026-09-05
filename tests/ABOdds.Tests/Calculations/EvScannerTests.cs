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

    [Theory]
    [InlineData("0.565", "2", true)]
    [InlineData("0.535", "2", true)]
    [InlineData("0.5349", "2", false)]
    [InlineData("0.5651", "2", false)]
    [InlineData("0.535", "3", false)]
    public void Scan_ValidatesAbsoluteEvDifferenceAtTheTargetPrice(string probability, string price, bool accepted)
    {
        var fair = CalculationTestData.FairValue(0.55m);
        fair = fair with
        {
            Sources =
            [
                fair.Sources[0] with { Role = FairValueSourceRole.Primary },
                fair.Sources[0] with
                {
                    BookmakerKey = "betonlineag", Role = FairValueSourceRole.Validation,
                    NoVigProbability = decimal.Parse(probability, System.Globalization.CultureInfo.InvariantCulture)
                }
            ]
        };
        var quote = CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team A",
            decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));
        var result = EvScanner.Scan([quote], [fair], CalculationTestData.Now, MaximumSourceAge, Options(0.03m, "fanduel"));
        Assert.Equal(accepted ? 1 : 0, result.Count);
        if (accepted) Assert.Equal(0.55m, result[0].FairProbability);
    }

    [Theory]
    [InlineData(FairValueSourceRole.Primary)]
    [InlineData(FairValueSourceRole.Fallback)]
    public void Scan_AllowsUnvalidatedAndFallbackOpportunities(FairValueSourceRole role)
    {
        var fair = CalculationTestData.FairValue(0.55m);
        fair = fair with { Sources = [fair.Sources[0] with
        {
            Role = role, BookmakerKey = role == FairValueSourceRole.Primary ? "pinnacle" : "betonlineag"
        }] };
        var quote = CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team A", 2m);
        var opportunity = Assert.Single(EvScanner.Scan([quote], [fair], CalculationTestData.Now,
            MaximumSourceAge, Options(0.03m, "fanduel")));
        Assert.Equal(role, Assert.Single(opportunity.Sources).Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_RejectsThreeWayTargetsWithoutDiscardingTwoWayTargets(bool staleDraw)
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 1.8m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 2.2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "betonlineag", "Draw", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Draw", 4m,
                sourceUpdatedAtUtc: staleDraw ? CalculationTestData.Now.AddSeconds(-91) : CalculationTestData.Now),
            CalculationTestData.Quote(MarketKeys.Moneyline, "draftkings", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "draftkings", "Team B", 2m)
        };
        var calculated = FairValueCalculator.Calculate(quotes, CalculationTestData.Now, MaximumSourceAge,
            new FairValueOptions { ReferenceBooks = [new() { Key = "pinnacle" }, new() { Key = "betonlineag" }] });
        var persisted = calculated.Select(value => new PersistedFairValue(Guid.NewGuid(), value.MarketId,
            value.SelectionKey, value.SelectionDisplayName, value.Line, value.FairProbability,
            value.FairDecimalOdds, value.Sources));

        var result = EvScanner.Scan(quotes, persisted, CalculationTestData.Now, MaximumSourceAge,
            Options(0.03m, "fanduel", "draftkings"));

        var opportunity = Assert.Single(result);
        Assert.Equal("draftkings", opportunity.BookmakerKey);
        Assert.Equal("team a", opportunity.SelectionKey);
        Assert.InRange(opportunity.ExpectedValue, 0.099999999m, 0.100000001m);
    }

    [Fact]
    public void Scan_RejectsPersistedFairValuesFromThreeWayReferences()
    {
        var quotes = new[]
        {
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team A", 2m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Team B", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "pinnacle", "Draw", 4m),
            CalculationTestData.Quote(MarketKeys.Moneyline, "fanduel", "Team A", 2.2m)
        };
        var result = EvScanner.Scan(quotes, [CalculationTestData.FairValue(0.5m)], CalculationTestData.Now,
            MaximumSourceAge, Options(0.03m, "fanduel"));

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
