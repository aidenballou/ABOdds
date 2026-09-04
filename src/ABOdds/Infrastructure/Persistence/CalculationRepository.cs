using System.Text.Json;
using ABOdds.Domain;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Infrastructure.Persistence;

public sealed record BatchQuotes(
    Guid BatchId,
    string SportKey,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<MarketQuote> Quotes);

public sealed record AlertBatchData(
    Guid BatchId,
    string SportKey,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<(Guid OpportunityId, CalculatedEvOpportunity Opportunity)> Opportunities);

public sealed class CalculationRepository(IDbContextFactory<BettingDbContext> contextFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<Guid?> GetNextFairValueBatchAsync(CancellationToken cancellationToken) =>
        GetNextBatchAsync(
            value => value.FairValueCompletedAtUtc == null,
            cancellationToken);

    public Task<Guid?> GetNextEvBatchAsync(CancellationToken cancellationToken) =>
        GetNextBatchAsync(
            value => value.FairValueCompletedAtUtc != null && value.EvCompletedAtUtc == null,
            cancellationToken);

    public Task<Guid?> GetNextAlertBatchAsync(CancellationToken cancellationToken) =>
        GetNextBatchAsync(
            value => value.EvCompletedAtUtc != null && value.AlertRulesCompletedAtUtc == null,
            cancellationToken);

    public async Task<BatchQuotes?> LoadBatchQuotesAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dbContext.PollBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var quotes = await dbContext.OddsSnapshots
            .AsNoTracking()
            .Where(value => value.PollBatchId == batchId)
            .Select(value => new MarketQuote(
                value.Id,
                value.Market.EventId,
                value.MarketId,
                value.Market.Event.ProviderEventId,
                value.Market.Event.SportKey,
                value.Market.Event.HomeTeam,
                value.Market.Event.AwayTeam,
                value.Market.Event.CommenceTimeUtc,
                value.Market.Key,
                value.Market.Period,
                value.BookmakerKey,
                value.BookmakerTitle,
                value.SelectionKey,
                value.SelectionDisplayName,
                value.DecimalOdds,
                value.Line,
                value.ObservedAtUtc,
                value.SourceUpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new BatchQuotes(batch.Id, batch.SportKey, batch.ObservedAtUtc, quotes);
    }

    public async Task<bool> SaveFairValuesAsync(
        Guid batchId,
        IReadOnlyList<CalculatedFairValue> fairValues,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var batch = await dbContext.PollBatches.SingleAsync(value => value.Id == batchId, cancellationToken);
        if (batch.FairValueCompletedAtUtc is not null)
        {
            return false;
        }

        var priorValues = dbContext.FairValues.Where(value => value.PollBatchId == batchId);
        dbContext.FairValues.RemoveRange(priorValues);
        foreach (var fairValue in fairValues)
        {
            dbContext.FairValues.Add(new FairValueEntity
            {
                Id = Guid.NewGuid(),
                PollBatchId = batchId,
                MarketId = fairValue.MarketId,
                SelectionKey = fairValue.SelectionKey,
                SelectionDisplayName = fairValue.SelectionDisplayName,
                Line = fairValue.Line,
                LineKey = fairValue.LineKey,
                FairProbability = fairValue.FairProbability,
                FairDecimalOdds = fairValue.FairDecimalOdds,
                ReferenceBookCount = fairValue.Sources.Count,
                SourcesJson = JsonSerializer.Serialize(fairValue.Sources, JsonOptions),
                CalculatedAtUtc = calculatedAtUtc
            });
        }

        batch.FairValueCompletedAtUtc = calculatedAtUtc;
        ClearFailure(batch);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PersistedFairValue>> LoadFairValuesAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.FairValues
            .AsNoTracking()
            .Where(value => value.PollBatchId == batchId)
            .ToListAsync(cancellationToken);

        return entities.Select(value => new PersistedFairValue(
                value.Id,
                value.MarketId,
                value.SelectionKey,
                value.SelectionDisplayName,
                value.Line,
                value.FairProbability,
                value.FairDecimalOdds,
                DeserializeSources(value.SourcesJson)))
            .ToArray();
    }

    public async Task<bool> SaveEvOpportunitiesAsync(
        Guid batchId,
        IReadOnlyList<CalculatedEvOpportunity> opportunities,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var batch = await dbContext.PollBatches.SingleAsync(value => value.Id == batchId, cancellationToken);
        if (batch.EvCompletedAtUtc is not null)
        {
            return false;
        }

        var priorOpportunities = dbContext.EvOpportunities.Where(value => value.PollBatchId == batchId);
        dbContext.EvOpportunities.RemoveRange(priorOpportunities);
        foreach (var opportunity in opportunities)
        {
            dbContext.EvOpportunities.Add(new EvOpportunityEntity
            {
                Id = Guid.NewGuid(),
                PollBatchId = batchId,
                FairValueId = opportunity.FairValueId,
                OddsSnapshotId = opportunity.OddsSnapshotId,
                ExpectedValue = opportunity.ExpectedValue,
                DetectedAtUtc = detectedAtUtc
            });
        }

        batch.EvCompletedAtUtc = detectedAtUtc;
        ClearFailure(batch);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AlertBatchData?> LoadAlertBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dbContext.PollBatches
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var entities = await dbContext.EvOpportunities
            .AsNoTracking()
            .Where(value => value.PollBatchId == batchId)
            .Include(value => value.OddsSnapshot)
                .ThenInclude(value => value.Market)
                .ThenInclude(value => value.Event)
            .Include(value => value.FairValue)
            .ToListAsync(cancellationToken);

        var opportunities = entities.Select(value =>
        {
            var quote = value.OddsSnapshot;
            var market = quote.Market;
            var fairValue = value.FairValue;
            var opportunity = new CalculatedEvOpportunity(
                fairValue.Id,
                quote.Id,
                market.EventId,
                market.Id,
                market.Event.ProviderEventId,
                market.Event.SportKey,
                market.Event.HomeTeam,
                market.Event.AwayTeam,
                market.Event.CommenceTimeUtc,
                market.Key,
                quote.BookmakerKey,
                quote.BookmakerTitle,
                quote.SelectionKey,
                quote.SelectionDisplayName,
                quote.Line,
                quote.DecimalOdds,
                fairValue.FairProbability,
                fairValue.FairDecimalOdds,
                value.ExpectedValue,
                DeserializeSources(fairValue.SourcesJson));
            return (value.Id, opportunity);
        }).ToArray();

        return new AlertBatchData(batch.Id, batch.SportKey, batch.ObservedAtUtc, opportunities);
    }

    public async Task RecordFailureAsync(
        Guid batchId,
        string stage,
        Exception exception,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await dbContext.PollBatches.SingleOrDefaultAsync(value => value.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return;
        }

        batch.LastFailedStage = stage;
        batch.LastError = exception.ToString()[..Math.Min(exception.ToString().Length, 8000)];
        batch.LastFailedAtUtc = failedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid?> GetNextBatchAsync(
        System.Linq.Expressions.Expression<Func<PollBatchEntity, bool>> predicate,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.PollBatches
            .AsNoTracking()
            .Where(predicate)
            .OrderBy(value => value.LastFailedAtUtc.HasValue)
            .ThenBy(value => value.LastFailedAtUtc)
            .ThenBy(value => value.ObservedAtUtc)
            .Select(value => (Guid?)value.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IReadOnlyList<FairValueSource> DeserializeSources(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<FairValueSource>>(json, JsonOptions) ?? [];

    private static void ClearFailure(PollBatchEntity batch)
    {
        batch.LastFailedStage = null;
        batch.LastError = null;
        batch.LastFailedAtUtc = null;
    }
}
