using ABOdds.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Services;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("baseball_mlb", "baseball_mlb")]
    [InlineData(" AMERICANFOOTBALL_NFL , baseball_mlb ", "americanfootball_nfl,baseball_mlb")]
    [InlineData("americanfootball_ncaaf", "americanfootball_ncaaf")]
    public void EnabledSports_ReplacesDefaultsWithExactlyTheSelectedSports(string setting, string expected)
    {
        var builder = ApplicationHost.CreateBuilder(["--OddsApi:EnabledSports=" + setting]);
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<OddsApiOptions>>().Value;
        Assert.Equal(expected.Split(','), options.Sports);
    }

    [Fact]
    public void Defaults_EnableNflCollegeFootballAndMlb()
    {
        using var host = ApplicationHost.CreateBuilder([]).Build();
        Assert.Equal(["americanfootball_nfl", "americanfootball_ncaaf", "baseball_mlb"],
            host.Services.GetRequiredService<IOptions<OddsApiOptions>>().Value.Sports);
    }

    [Theory]
    [InlineData("OddsApi", "OddsApi:EnabledSports", "americanfootball_nfl, AMERICANFOOTBALL_NFL ")]
    [InlineData("OddsApi", "OddsApi:EnabledSports", "baseball_mlb,unknown_sport")]
    [InlineData("OddsApi", "OddsApi:EnabledSports", "")]
    [InlineData("OddsApi", "OddsApi:EnabledSports", " ")]
    [InlineData("OddsApi", "OddsApi:EnabledSports", "baseball_mlb,")]
    [InlineData("OddsApi", "OddsApi:Markets:1", "h2h")]
    [InlineData("OddsApi", "OddsApi:Markets:1", "player_pass_yds")]
    [InlineData("OddsApi", "OddsApi:BaseUrl", "relative/path")]
    [InlineData("OddsApi", "OddsApi:RequestTimeout", "00:00:00")]
    [InlineData("Ev", "Ev:MaximumReferenceEvDifference", "0")]
    [InlineData("Ev", "Ev:MaximumReferenceEvDifference", "-0.01")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Key", " pinnacle ")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Key", " ")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Key", "lowvig")]
    [InlineData("Ev", "Ev:TargetBooks:0:Key", " pinnacle ")]
    [InlineData("Ev", "Ev:TargetBooks:1:Key", " FANDUEL ")]
    [InlineData("Ev", "Ev:TargetBooks:0:Key", " ")]
    [InlineData("Ev", "Ev:RealertImprovement", "0")]
    [InlineData("Ev", "Ev:MinimumExpectedValue", "0")]
    public void StartupOptions_RejectUnsafeConfiguration(string section, string key, string value)
    {
        var builder = ApplicationHost.CreateBuilder([]);
        builder.Logging.ClearProviders();
        builder.Configuration[key] = value;
        using var host = builder.Build();
        Assert.Throws<OptionsValidationException>(() =>
        {
            _ = section switch
            {
                "OddsApi" => (object)host.Services.GetRequiredService<IOptions<OddsApiOptions>>().Value,
                "FairValue" => host.Services.GetRequiredService<IOptions<FairValueOptions>>().Value,
                _ => host.Services.GetRequiredService<IOptions<EvOptions>>().Value
            };
        });
    }

    [Fact]
    public void EleventhBook_IsRejectedBeforeRequestConstruction()
    {
        var builder = ApplicationHost.CreateBuilder([]);
        builder.Configuration["Ev:TargetBooks:7:Key"] = "betrivers";
        builder.Configuration["Ev:TargetBooks:8:Key"] = "circa";
        using var host = builder.Build();
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<EvOptions>>().Value);
    }

    [Fact]
    public void Defaults_HaveNineDisjointBooksAndThreePointValidationTolerance()
    {
        var builder = ApplicationHost.CreateBuilder([]);
        using var host = builder.Build();
        var references = host.Services.GetRequiredService<IOptions<FairValueOptions>>().Value;
        var ev = host.Services.GetRequiredService<IOptions<EvOptions>>().Value;
        Assert.Equal(0.03m, ev.MaximumReferenceEvDifference);
        Assert.Collection(references.ReferenceBooks,
            book => Assert.Equal("pinnacle", book.Key), book => Assert.Equal("betonlineag", book.Key));
        Assert.Equal(9, references.ReferenceBooks.Select(book => book.Key).Concat(ev.TargetBooks.Select(book => book.Key)).Distinct().Count());
    }
}
