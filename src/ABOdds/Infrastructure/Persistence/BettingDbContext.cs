using ABOdds.Domain;
using Microsoft.EntityFrameworkCore;

namespace ABOdds.Infrastructure.Persistence;

public sealed class BettingDbContext(DbContextOptions<BettingDbContext> options) : DbContext(options)
{
    public DbSet<PollBatchEntity> PollBatches => Set<PollBatchEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<MarketEntity> Markets => Set<MarketEntity>();
    public DbSet<OddsSnapshotEntity> OddsSnapshots => Set<OddsSnapshotEntity>();
    public DbSet<FairValueEntity> FairValues => Set<FairValueEntity>();
    public DbSet<EvOpportunityEntity> EvOpportunities => Set<EvOpportunityEntity>();
    public DbSet<AlertStateEntity> AlertStates => Set<AlertStateEntity>();
    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PollBatchEntity>(entity =>
        {
            entity.ToTable("poll_batches");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Provider).HasMaxLength(32);
            entity.Property(value => value.SportKey).HasMaxLength(64);
            entity.Property(value => value.LastFailedStage).HasMaxLength(32);
            entity.HasIndex(value => new { value.FairValueCompletedAtUtc, value.ObservedAtUtc });
            entity.HasIndex(value => new { value.EvCompletedAtUtc, value.ObservedAtUtc });
            entity.HasIndex(value => new { value.AlertRulesCompletedAtUtc, value.ObservedAtUtc });
        });

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Provider).HasMaxLength(32);
            entity.Property(value => value.ProviderEventId).HasMaxLength(128);
            entity.Property(value => value.SportKey).HasMaxLength(64);
            entity.Property(value => value.HomeTeam).HasMaxLength(160);
            entity.Property(value => value.AwayTeam).HasMaxLength(160);
            entity.HasIndex(value => new { value.Provider, value.ProviderEventId }).IsUnique();
            entity.HasIndex(value => value.CommenceTimeUtc);
        });

        modelBuilder.Entity<MarketEntity>(entity =>
        {
            entity.ToTable("markets");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Key).HasMaxLength(32);
            entity.Property(value => value.Period).HasMaxLength(32);
            entity.HasIndex(value => new { value.EventId, value.Key, value.Period }).IsUnique();
            entity.HasOne(value => value.Event)
                .WithMany(value => value.Markets)
                .HasForeignKey(value => value.EventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OddsSnapshotEntity>(entity =>
        {
            entity.ToTable("odds_snapshots");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.BookmakerKey).HasMaxLength(64);
            entity.Property(value => value.BookmakerTitle).HasMaxLength(128);
            entity.Property(value => value.SelectionKey).HasMaxLength(200);
            entity.Property(value => value.SelectionDisplayName).HasMaxLength(200);
            entity.Property(value => value.DecimalOdds).HasPrecision(20, 10);
            entity.Property(value => value.Line).HasPrecision(12, 4);
            entity.Property(value => value.LineKey).HasMaxLength(32);
            entity.HasIndex(value => new
            {
                value.PollBatchId,
                value.MarketId,
                value.BookmakerKey,
                value.SelectionKey,
                value.LineKey
            }).IsUnique();
            entity.HasIndex(value => new { value.MarketId, value.SourceUpdatedAtUtc });
            entity.HasOne(value => value.PollBatch)
                .WithMany(value => value.OddsSnapshots)
                .HasForeignKey(value => value.PollBatchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.Market)
                .WithMany(value => value.OddsSnapshots)
                .HasForeignKey(value => value.MarketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FairValueEntity>(entity =>
        {
            entity.ToTable("fair_values");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.SelectionKey).HasMaxLength(200);
            entity.Property(value => value.SelectionDisplayName).HasMaxLength(200);
            entity.Property(value => value.Line).HasPrecision(12, 4);
            entity.Property(value => value.LineKey).HasMaxLength(32);
            entity.Property(value => value.FairProbability).HasPrecision(20, 12);
            entity.Property(value => value.FairDecimalOdds).HasPrecision(20, 10);
            entity.Property(value => value.SourcesJson).HasColumnType("jsonb");
            entity.HasIndex(value => new
            {
                value.PollBatchId,
                value.MarketId,
                value.SelectionKey,
                value.LineKey
            }).IsUnique();
            entity.HasIndex(value => new { value.MarketId, value.CalculatedAtUtc });
            entity.HasOne(value => value.PollBatch)
                .WithMany(value => value.FairValues)
                .HasForeignKey(value => value.PollBatchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.Market)
                .WithMany(value => value.FairValues)
                .HasForeignKey(value => value.MarketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EvOpportunityEntity>(entity =>
        {
            entity.ToTable("ev_opportunities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.ExpectedValue).HasPrecision(20, 12);
            entity.HasIndex(value => new { value.PollBatchId, value.OddsSnapshotId }).IsUnique();
            entity.HasIndex(value => value.DetectedAtUtc);
            entity.HasOne(value => value.PollBatch)
                .WithMany(value => value.EvOpportunities)
                .HasForeignKey(value => value.PollBatchId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.FairValue)
                .WithMany(value => value.EvOpportunities)
                .HasForeignKey(value => value.FairValueId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.OddsSnapshot)
                .WithMany()
                .HasForeignKey(value => value.OddsSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlertStateEntity>(entity =>
        {
            entity.ToTable("alert_states");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.SelectionKey).HasMaxLength(200);
            entity.Property(value => value.LineKey).HasMaxLength(32);
            entity.Property(value => value.BookmakerKey).HasMaxLength(64);
            entity.Property(value => value.LastSeenExpectedValue).HasPrecision(20, 12);
            entity.Property(value => value.LastSeenDecimalOdds).HasPrecision(20, 10);
            entity.Property(value => value.LastAlertedExpectedValue).HasPrecision(20, 12);
            entity.Property(value => value.LastAlertedDecimalOdds).HasPrecision(20, 10);
            entity.HasIndex(value => new
            {
                value.EventId,
                value.MarketId,
                value.SelectionKey,
                value.LineKey,
                value.BookmakerKey
            }).IsUnique();
            entity.HasOne(value => value.Event)
                .WithMany()
                .HasForeignKey(value => value.EventId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.Market)
                .WithMany(value => value.AlertStates)
                .HasForeignKey(value => value.MarketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlertEntity>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Reason).HasConversion<string>().HasMaxLength(32);
            entity.Property(value => value.DeliveryStatus).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(value => new { value.DeliveryStatus, value.NextAttemptAtUtc });
            entity.HasOne(value => value.AlertState)
                .WithMany(value => value.Alerts)
                .HasForeignKey(value => value.AlertStateId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.EvOpportunity)
                .WithMany(value => value.Alerts)
                .HasForeignKey(value => value.EvOpportunityId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
