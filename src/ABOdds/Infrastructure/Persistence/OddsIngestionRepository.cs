using ABOdds.Domain;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Infrastructure.Persistence;

public sealed class OddsIngestionRepository(IDbContextFactory<BettingDbContext> contextFactory)
{
    public async Task<Guid> SaveAsync(NormalizedOddsBatch normalizedBatch, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.PollBatches.AnyAsync(value => value.Id == normalizedBatch.Id, cancellationToken))
        {
            return normalizedBatch.Id;
        }

        var batch = new PollBatchEntity
        {
            Id = normalizedBatch.Id,
            SportKey = normalizedBatch.SportKey,
            ObservedAtUtc = normalizedBatch.ObservedAtUtc,
            QuotaRemaining = normalizedBatch.Quota.Remaining,
            QuotaUsed = normalizedBatch.Quota.Used,
            RequestCost = normalizedBatch.Quota.LastRequestCost
        };
        dbContext.PollBatches.Add(batch);

        var normalizedEvents = normalizedBatch.Events
            .GroupBy(value => value.ProviderEventId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        batch.EventMetadataJson = JsonSerializer.Serialize(normalizedEvents.Select(game => new ObservedEventMetadata(
            game.ProviderEventId, game.SportKey, game.HomeTeam, game.AwayTeam, game.CommenceTimeUtc)));
        var providerEventIds = normalizedEvents.Select(value => value.ProviderEventId).ToArray();
        var existingEvents = await dbContext.Events
            .Where(value => value.Provider == ABOdds.Domain.Providers.TheOddsApi && providerEventIds.Contains(value.ProviderEventId))
            .ToDictionaryAsync(value => value.ProviderEventId, StringComparer.Ordinal, cancellationToken);

        foreach (var normalizedEvent in normalizedEvents)
        {
            if (!existingEvents.TryGetValue(normalizedEvent.ProviderEventId, out var eventEntity))
            {
                eventEntity = new EventEntity
                {
                    Id = Guid.NewGuid(),
                    ProviderEventId = normalizedEvent.ProviderEventId,
                    FirstSeenAtUtc = normalizedBatch.ObservedAtUtc
                };
                existingEvents.Add(normalizedEvent.ProviderEventId, eventEntity);
                dbContext.Events.Add(eventEntity);
            }

            eventEntity.SportKey = normalizedEvent.SportKey;
            eventEntity.HomeTeam = normalizedEvent.HomeTeam;
            eventEntity.AwayTeam = normalizedEvent.AwayTeam;
            eventEntity.CommenceTimeUtc = normalizedEvent.CommenceTimeUtc;
            eventEntity.LastSeenAtUtc = normalizedBatch.ObservedAtUtc;
        }

        var eventIds = existingEvents.Values.Select(value => value.Id).ToArray();
        var existingMarkets = await dbContext.Markets
            .Where(value => eventIds.Contains(value.EventId))
            .ToListAsync(cancellationToken);
        var marketsByKey = existingMarkets.ToDictionary(
            value => (value.EventId, value.Key, value.Period),
            value => value);

        foreach (var normalizedEvent in normalizedEvents)
        {
            var eventEntity = existingEvents[normalizedEvent.ProviderEventId];
            foreach (var marketKey in normalizedEvent.Quotes
                         .Select(value => (value.MarketKey, value.Period))
                         .Distinct())
            {
                var key = (eventEntity.Id, marketKey.MarketKey, marketKey.Period);
                if (!marketsByKey.ContainsKey(key))
                {
                    var market = new MarketEntity
                    {
                        Id = Guid.NewGuid(),
                        EventId = eventEntity.Id,
                        Key = marketKey.MarketKey,
                        Period = marketKey.Period
                    };
                    marketsByKey.Add(key, market);
                    dbContext.Markets.Add(market);
                }
            }

            var uniqueQuotes = normalizedEvent.Quotes
                .GroupBy(value => new
                {
                    value.MarketKey,
                    value.Period,
                    value.BookmakerKey,
                    value.SelectionKey,
                    LineKey = OddsKey.FormatLine(value.Line)
                })
                .Select(group => group.OrderByDescending(value => value.SourceUpdatedAtUtc).First());

            foreach (var quote in uniqueQuotes)
            {
                var market = marketsByKey[(eventEntity.Id, quote.MarketKey, quote.Period)];
                dbContext.OddsSnapshots.Add(new OddsSnapshotEntity
                {
                    Id = Guid.NewGuid(),
                    PollBatchId = batch.Id,
                    MarketId = market.Id,
                    BookmakerKey = quote.BookmakerKey,
                    BookmakerTitle = quote.BookmakerTitle,
                    SelectionKey = quote.SelectionKey,
                    SelectionDisplayName = quote.SelectionDisplayName,
                    DecimalOdds = quote.DecimalOdds,
                    Line = quote.Line,
                    LineKey = OddsKey.FormatLine(quote.Line),
                    ObservedAtUtc = normalizedBatch.ObservedAtUtc,
                    SourceUpdatedAtUtc = quote.SourceUpdatedAtUtc
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }
}
