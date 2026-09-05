using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Infrastructure.Persistence;
using ABOdds.Services.Alerts;
using ABOdds.Services.Calculations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ABOdds.Tests.Persistence;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ABODDS_TEST_POSTGRES")))
        {
            Skip = "Set ABODDS_TEST_POSTGRES to run real PostgreSQL tests. Each test creates its own disposable database.";
        }
    }
}

[Trait("Category", "Postgres")]
public abstract class PostgresTest : IAsyncLifetime
{
    private ServiceProvider _services = null!;
    protected IDbContextFactory<BettingDbContext> Factory { get; private set; } = null!;
    protected OddsIngestionRepository Ingestion { get; private set; } = null!;
    protected CalculationRepository Calculations { get; private set; } = null!;
    protected AlertRepository Alerts { get; private set; } = null!;
    protected static DateTimeOffset Now => new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    protected static FairValueOptions References => new()
    {
        ReferenceBooks =
        [
            new() { Key = "pinnacle", DisplayName = "Pinnacle", Weight = 0.5m },
            new() { Key = "betonlineag", DisplayName = "BetOnline", Weight = 0.3m },
            new() { Key = "lowvig", DisplayName = "LowVig", Weight = 0.2m }
        ]
    };
    protected static EvOptions Ev => new()
    {
        TargetBooks = [new() { Key = "fanduel", DisplayName = "FanDuel" }]
    };

    public async Task InitializeAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ABODDS_TEST_POSTGRES"))
        {
            Database = "abodds_test_" + Guid.NewGuid().ToString("N")
        };
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<BettingDbContext>(options =>
            BettingDbContext.ConfigurePostgres(options, connection.ConnectionString));
        _services = services.BuildServiceProvider();
        Factory = _services.GetRequiredService<IDbContextFactory<BettingDbContext>>();
        Ingestion = new(Factory);
        Calculations = new(Factory);
        Alerts = new(Factory, new AlertDecisionService(Options.Create(Ev)));
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is null) return;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            // Only the randomly named database created by this test is removed.
            await db.Database.EnsureDeletedAsync();
        }
        await _services.DisposeAsync();
    }

    protected static NormalizedOddsBatch Batch(DateTimeOffset? observedAt = null, decimal targetPrice = 2m)
    {
        var time = observedAt ?? Now;
        var quotes = References.ReferenceBooks.SelectMany(book => new[]
        {
            new NormalizedQuote("spreads", "pregame", book.Key, book.DisplayName,
                "indianapolis colts", "Indianapolis Colts", 1.8m, 3.5m, time),
            new NormalizedQuote("spreads", "pregame", book.Key, book.DisplayName,
                "houston texans", "Houston Texans", 2.1m, -3.5m, time)
        }).Append(new NormalizedQuote("spreads", "pregame", "fanduel", "FanDuel",
            "indianapolis colts", "Indianapolis Colts", targetPrice, 3.5m, time)).ToArray();
        return new("americanfootball_nfl", time,
            [new("event-1", "americanfootball_nfl", "Houston Texans", "Indianapolis Colts", Now.AddHours(2), quotes)],
            new(1000, 3, 3));
    }

    protected async Task<AlertBatchData> ProcessAsync(NormalizedOddsBatch batch)
    {
        var id = await Ingestion.SaveAsync(batch, default);
        var quotes = (await Calculations.LoadBatchQuotesAsync(id, default))!.Quotes;
        var fair = FairValueCalculator.Calculate(quotes, batch.ObservedAtUtc, TimeSpan.FromSeconds(90), References);
        await Calculations.SaveFairValuesAsync(id, fair, batch.ObservedAtUtc, default);
        var stored = await Calculations.LoadFairValuesAsync(id, default);
        var opportunities = EvScanner.Scan(quotes, stored, batch.ObservedAtUtc, TimeSpan.FromSeconds(90), Ev);
        await Calculations.SaveEvOpportunitiesAsync(id, opportunities, batch.ObservedAtUtc, default);
        return (await Calculations.LoadAlertBatchAsync(id, default))!;
    }
}
