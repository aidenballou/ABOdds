using ABOdds.Configuration;
using ABOdds.Domain;
using ABOdds.Pipeline;
using ABOdds.Providers;
using ABOdds.Services;
using ABOdds.Time;
using ABOdds.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ABOdds.Tests.Persistence;

public sealed class PollingWorkerTests : PostgresTest
{
    [PostgresFact]
    public async Task RunOnce_MakesOneRequestPerSportAndPersistsCompletePipeline()
    {
        var provider = new FakeProvider();
        using var worker = CreateWorker(provider, new PollingOptions { RunOnce = true });
        await RunToCompletionAsync(worker);
        Assert.Equal(0, worker.ExitCode);
        Assert.Collection(provider.Calls,
            sport => Assert.Equal("americanfootball_nfl", sport),
            sport => Assert.Equal("americanfootball_ncaaf", sport));
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.PollBatches.CountAsync(value => value.AlertRulesCompletedAtUtc != null));
        Assert.Equal(10, await db.OddsSnapshots.CountAsync());
        Assert.Equal(2, await db.Alerts.CountAsync());
    }

    [PostgresFact]
    public async Task InvalidIngestion_StopsBeforeSecondPaidRequest()
    {
        var provider = new FakeProvider { InvalidQuote = true };
        using var worker = CreateWorker(provider, new PollingOptions());
        await RunToCompletionAsync(worker);
        Assert.Equal(1, worker.ExitCode);
        Assert.Single(provider.Calls);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PollBatches.CountAsync());
    }

    [PostgresFact]
    public async Task DownstreamFailure_StopsPaidRequestsAndKeepsPaidSnapshotForReplay()
    {
        await RejectFairValuesAsync();
        var provider = new FakeProvider();
        using var worker = CreateWorker(provider, new PollingOptions());
        await RunToCompletionAsync(worker);
        Assert.Equal(1, worker.ExitCode);
        Assert.Single(provider.Calls);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(5, await db.OddsSnapshots.CountAsync());
        Assert.Equal("fair-value", (await db.PollBatches.SingleAsync()).LastFailedStage);
        Assert.Equal(0, await db.FairValues.CountAsync());
    }

    [PostgresFact]
    public async Task BrokenPendingBatchOnRestart_PreventsAnyNewPaidRequest()
    {
        await Ingestion.SaveAsync(Batch(), default);
        await RejectFairValuesAsync();
        var provider = new FakeProvider();
        using var worker = CreateWorker(provider, new PollingOptions());
        await RunToCompletionAsync(worker);
        Assert.Equal(1, worker.ExitCode);
        Assert.Empty(provider.Calls);
    }

    [PostgresFact]
    public async Task Restart_ReplaysPaidSnapshotBeforeCheckingBudget()
    {
        var batch = Batch();
        var id = await Ingestion.SaveAsync(batch, default);
        Assert.Equal(id, await Ingestion.SaveAsync(batch, default));
        var provider = new FakeProvider();
        using var worker = CreateWorker(provider, new PollingOptions { MaximumCreditsPerRun = 2 });
        await RunToCompletionAsync(worker);
        Assert.Equal(0, worker.ExitCode);
        Assert.Empty(provider.Calls);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(5, await db.OddsSnapshots.CountAsync());
        Assert.Equal(1, await db.Alerts.CountAsync());
        Assert.NotNull((await db.PollBatches.SingleAsync()).AlertRulesCompletedAtUtc);
    }

    [PostgresFact]
    public async Task CreditCap_StopsBeforeRequestThatWouldExceedCap()
    {
        var provider = new FakeProvider();
        using var worker = CreateWorker(provider, new PollingOptions { MaximumCreditsPerRun = 3 });
        await RunToCompletionAsync(worker);
        Assert.Equal(0, worker.ExitCode);
        Assert.Single(provider.Calls);
    }

    [PostgresFact]
    public async Task LowProviderQuota_StopsBeforeNextPaidRequest()
    {
        var provider = new FakeProvider { Remaining = 2 };
        using var worker = CreateWorker(provider, new PollingOptions());
        await RunToCompletionAsync(worker);
        Assert.Equal(0, worker.ExitCode);
        Assert.Single(provider.Calls);
    }

    [PostgresFact]
    public async Task MissingQuotaHeaders_PersistsResponseButStopsFurtherRequests()
    {
        var provider = new FakeProvider { Remaining = null };
        using var worker = CreateWorker(provider, new PollingOptions());
        await RunToCompletionAsync(worker);
        Assert.Equal(1, worker.ExitCode);
        Assert.Single(provider.Calls);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.PollBatches.CountAsync());
    }

    private OddsPollingWorker CreateWorker(IOddsProvider provider, PollingOptions polling)
    {
        var options = Options.Create(polling);
        var clock = new FixedClock();
        var pipeline = new OddsPipeline(Calculations, Alerts, options,
            Options.Create(References), Options.Create(Ev), new SemaphoreSlim(1, 1), clock);
        return new(provider, Ingestion, pipeline, new AdaptivePollingSchedule(polling),
            Options.Create(new OddsApiOptions { Enabled = true, EnabledSports = "americanfootball_nfl,americanfootball_ncaaf" }),
            options, new TestLifetime(), clock, NullLogger<OddsPollingWorker>.Instance);
    }

    private static async Task RunToCompletionAsync(OddsPollingWorker worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(timeout.Token);
        await worker.ExecuteTask!.WaitAsync(timeout.Token);
    }

    private async Task RejectFairValuesAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_fair_value() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic persistence failure'; END; $$;
            CREATE TRIGGER reject_fair_value BEFORE INSERT ON fair_values
            FOR EACH ROW EXECUTE FUNCTION reject_fair_value();
            """);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private sealed class FakeProvider : IOddsProvider
    {
        public int EstimatedRequestCost => 3;
        public List<string> Calls { get; } = [];
        public bool InvalidQuote { get; init; }
        public int? Remaining { get; init; } = 1000;
        public Task<NormalizedOddsBatch> GetOddsAsync(string sportKey, CancellationToken cancellationToken = default)
        {
            Calls.Add(sportKey);
            var batch = Batch();
            var game = batch.Events.Single() with { ProviderEventId = sportKey, SportKey = sportKey };
            if (InvalidQuote) game = game with { Quotes = [game.Quotes[0] with { SelectionKey = new string('x', 201) }] };
            return Task.FromResult(batch with { SportKey = sportKey, Events = [game], Quota = new(Remaining, 3, 3) });
        }
    }
}
