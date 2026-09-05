using ABOdds.Domain;

namespace ABOdds.Infrastructure.Persistence;

public sealed class PollBatchEntity
{
    public string? EventMetadataJson { get; set; }
    public Guid Id { get; set; }
    public string Provider { get; set; } = ABOdds.Domain.Providers.TheOddsApi;
    public string SportKey { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public int? QuotaRemaining { get; set; }
    public int? QuotaUsed { get; set; }
    public int? RequestCost { get; set; }
    public DateTimeOffset? FairValueCompletedAtUtc { get; set; }
    public DateTimeOffset? EvCompletedAtUtc { get; set; }
    public DateTimeOffset? AlertRulesCompletedAtUtc { get; set; }
    public string? LastFailedStage { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastFailedAtUtc { get; set; }
    public ICollection<OddsSnapshotEntity> OddsSnapshots { get; set; } = [];
    public ICollection<FairValueEntity> FairValues { get; set; } = [];
    public ICollection<EvOpportunityEntity> EvOpportunities { get; set; } = [];
}

public sealed class EventEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = ABOdds.Domain.Providers.TheOddsApi;
    public string ProviderEventId { get; set; } = string.Empty;
    public string SportKey { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateTimeOffset CommenceTimeUtc { get; set; }
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public ICollection<MarketEntity> Markets { get; set; } = [];
}

public sealed class MarketEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public EventEntity Event { get; set; } = null!;
    public string Key { get; set; } = string.Empty;
    public string Period { get; set; } = MarketPeriods.Pregame;
    public ICollection<OddsSnapshotEntity> OddsSnapshots { get; set; } = [];
    public ICollection<FairValueEntity> FairValues { get; set; } = [];
    public ICollection<AlertStateEntity> AlertStates { get; set; } = [];
}

public sealed class OddsSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid PollBatchId { get; set; }
    public PollBatchEntity PollBatch { get; set; } = null!;
    public Guid MarketId { get; set; }
    public MarketEntity Market { get; set; } = null!;
    public string BookmakerKey { get; set; } = string.Empty;
    public string BookmakerTitle { get; set; } = string.Empty;
    public string SelectionKey { get; set; } = string.Empty;
    public string SelectionDisplayName { get; set; } = string.Empty;
    public decimal DecimalOdds { get; set; }
    public decimal? Line { get; set; }
    public string LineKey { get; set; } = OddsKey.FormatLine(null);
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset SourceUpdatedAtUtc { get; set; }
}

public sealed class FairValueEntity
{
    public string CalculationVersion { get; set; } = "legacy-unknown";
    public Guid Id { get; set; }
    public Guid PollBatchId { get; set; }
    public PollBatchEntity PollBatch { get; set; } = null!;
    public Guid MarketId { get; set; }
    public MarketEntity Market { get; set; } = null!;
    public string SelectionKey { get; set; } = string.Empty;
    public string SelectionDisplayName { get; set; } = string.Empty;
    public decimal? Line { get; set; }
    public string LineKey { get; set; } = OddsKey.FormatLine(null);
    public decimal FairProbability { get; set; }
    public decimal FairDecimalOdds { get; set; }
    public int ReferenceBookCount { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public DateTimeOffset CalculatedAtUtc { get; set; }
    public ICollection<EvOpportunityEntity> EvOpportunities { get; set; } = [];
}

public sealed class EvOpportunityEntity
{
    public Guid Id { get; set; }
    public Guid PollBatchId { get; set; }
    public PollBatchEntity PollBatch { get; set; } = null!;
    public Guid FairValueId { get; set; }
    public FairValueEntity FairValue { get; set; } = null!;
    public Guid OddsSnapshotId { get; set; }
    public OddsSnapshotEntity OddsSnapshot { get; set; } = null!;
    public decimal ExpectedValue { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
    public ICollection<AlertEntity> Alerts { get; set; } = [];
}

public sealed class AlertStateEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public EventEntity Event { get; set; } = null!;
    public Guid MarketId { get; set; }
    public MarketEntity Market { get; set; } = null!;
    public string SelectionKey { get; set; } = string.Empty;
    public string LineKey { get; set; } = string.Empty;
    public string BookmakerKey { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal LastSeenExpectedValue { get; set; }
    public decimal LastSeenDecimalOdds { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public decimal? LastAlertedExpectedValue { get; set; }
    public decimal? LastAlertedDecimalOdds { get; set; }
    public DateTimeOffset? LastAlertedAtUtc { get; set; }
    public DateTimeOffset? LastDisappearedAtUtc { get; set; }
    public ICollection<AlertEntity> Alerts { get; set; } = [];
}

public sealed class AlertEntity
{
    public Guid Id { get; set; }
    public Guid AlertStateId { get; set; }
    public AlertStateEntity AlertState { get; set; } = null!;
    public Guid EvOpportunityId { get; set; }
    public EvOpportunityEntity EvOpportunity { get; set; } = null!;
    public AlertReason Reason { get; set; }
    public string Message { get; set; } = string.Empty;
    public AlertDeliveryStatus DeliveryStatus { get; set; } = AlertDeliveryStatus.Pending;
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? RateLimitedUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? LastDeliveryError { get; set; }
}
