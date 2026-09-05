using ABOdds.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Services;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("OddsApi", "OddsApi:Sports:1", "americanfootball_nfl")]
    [InlineData("OddsApi", "OddsApi:Markets:1", "h2h")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Weight", "0")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Weight", "-1")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Key", " pinnacle ")]
    [InlineData("FairValue", "FairValue:ReferenceBooks:1:Key", " ")]
    [InlineData("FairValue", "FairValue:MinimumReferenceBooks", "1")]
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
        using var host = builder.Build();
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<EvOptions>>().Value);
    }

    [Fact]
    public void Defaults_HaveTenDisjointBooksAndRequestedWeights()
    {
        var builder = ApplicationHost.CreateBuilder([]);
        using var host = builder.Build();
        var references = host.Services.GetRequiredService<IOptions<FairValueOptions>>().Value;
        var ev = host.Services.GetRequiredService<IOptions<EvOptions>>().Value;
        Assert.Equal(2, references.MinimumReferenceBooks);
        Assert.Collection(references.ReferenceBooks,
            book => Assert.Equal(0.5m, book.Weight), book => Assert.Equal(0.3m, book.Weight), book => Assert.Equal(0.2m, book.Weight));
        Assert.Equal(10, references.ReferenceBooks.Select(book => book.Key).Concat(ev.TargetBooks.Select(book => book.Key)).Distinct().Count());
    }
}
