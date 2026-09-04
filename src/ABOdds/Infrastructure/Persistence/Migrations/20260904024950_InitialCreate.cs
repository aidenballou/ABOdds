using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABOdds.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SportKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HomeTeam = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AwayTeam = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CommenceTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "poll_batches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SportKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QuotaRemaining = table.Column<int>(type: "integer", nullable: true),
                    QuotaUsed = table.Column<int>(type: "integer", nullable: true),
                    RequestCost = table.Column<int>(type: "integer", nullable: true),
                    FairValueCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EvCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AlertRulesCompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailedStage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastFailedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "markets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_markets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_markets_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LineKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BookmakerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSeenExpectedValue = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: false),
                    LastSeenDecimalOdds = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAlertedExpectedValue = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: true),
                    LastAlertedDecimalOdds = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: true),
                    LastAlertedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDisappearedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_states_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_states_markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fair_values",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PollBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SelectionDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Line = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    LineKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FairProbability = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: false),
                    FairDecimalOdds = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    ReferenceBookCount = table.Column<int>(type: "integer", nullable: false),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CalculatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fair_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fair_values_markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fair_values_poll_batches_PollBatchId",
                        column: x => x.PollBatchId,
                        principalTable: "poll_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "odds_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PollBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookmakerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BookmakerTitle = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SelectionKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SelectionDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecimalOdds = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    Line = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    LineKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceUpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_odds_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_odds_snapshots_markets_MarketId",
                        column: x => x.MarketId,
                        principalTable: "markets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_odds_snapshots_poll_batches_PollBatchId",
                        column: x => x.PollBatchId,
                        principalTable: "poll_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ev_opportunities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PollBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    FairValueId = table.Column<Guid>(type: "uuid", nullable: false),
                    OddsSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedValue = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ev_opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ev_opportunities_fair_values_FairValueId",
                        column: x => x.FairValueId,
                        principalTable: "fair_values",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ev_opportunities_odds_snapshots_OddsSnapshotId",
                        column: x => x.OddsSnapshotId,
                        principalTable: "odds_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ev_opportunities_poll_batches_PollBatchId",
                        column: x => x.PollBatchId,
                        principalTable: "poll_batches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertStateId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvOpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DeliveryStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeliveryAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDeliveryError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alerts_alert_states_AlertStateId",
                        column: x => x.AlertStateId,
                        principalTable: "alert_states",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alerts_ev_opportunities_EvOpportunityId",
                        column: x => x.EvOpportunityId,
                        principalTable: "ev_opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_states_EventId_MarketId_SelectionKey_LineKey_Bookmake~",
                table: "alert_states",
                columns: new[] { "EventId", "MarketId", "SelectionKey", "LineKey", "BookmakerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_states_MarketId",
                table: "alert_states",
                column: "MarketId");

            migrationBuilder.CreateIndex(
                name: "IX_alerts_AlertStateId",
                table: "alerts",
                column: "AlertStateId");

            migrationBuilder.CreateIndex(
                name: "IX_alerts_DeliveryStatus_NextAttemptAtUtc",
                table: "alerts",
                columns: new[] { "DeliveryStatus", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_EvOpportunityId",
                table: "alerts",
                column: "EvOpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_ev_opportunities_DetectedAtUtc",
                table: "ev_opportunities",
                column: "DetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ev_opportunities_FairValueId",
                table: "ev_opportunities",
                column: "FairValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ev_opportunities_OddsSnapshotId",
                table: "ev_opportunities",
                column: "OddsSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_ev_opportunities_PollBatchId_OddsSnapshotId",
                table: "ev_opportunities",
                columns: new[] { "PollBatchId", "OddsSnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_events_CommenceTimeUtc",
                table: "events",
                column: "CommenceTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_events_Provider_ProviderEventId",
                table: "events",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fair_values_MarketId_CalculatedAtUtc",
                table: "fair_values",
                columns: new[] { "MarketId", "CalculatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_fair_values_PollBatchId_MarketId_SelectionKey_LineKey",
                table: "fair_values",
                columns: new[] { "PollBatchId", "MarketId", "SelectionKey", "LineKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_markets_EventId_Key_Period",
                table: "markets",
                columns: new[] { "EventId", "Key", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_odds_snapshots_MarketId_SourceUpdatedAtUtc",
                table: "odds_snapshots",
                columns: new[] { "MarketId", "SourceUpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_odds_snapshots_PollBatchId_MarketId_BookmakerKey_SelectionK~",
                table: "odds_snapshots",
                columns: new[] { "PollBatchId", "MarketId", "BookmakerKey", "SelectionKey", "LineKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_poll_batches_AlertRulesCompletedAtUtc_ObservedAtUtc",
                table: "poll_batches",
                columns: new[] { "AlertRulesCompletedAtUtc", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_batches_EvCompletedAtUtc_ObservedAtUtc",
                table: "poll_batches",
                columns: new[] { "EvCompletedAtUtc", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_batches_FairValueCompletedAtUtc_ObservedAtUtc",
                table: "poll_batches",
                columns: new[] { "FairValueCompletedAtUtc", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "alert_states");

            migrationBuilder.DropTable(
                name: "ev_opportunities");

            migrationBuilder.DropTable(
                name: "fair_values");

            migrationBuilder.DropTable(
                name: "odds_snapshots");

            migrationBuilder.DropTable(
                name: "markets");

            migrationBuilder.DropTable(
                name: "poll_batches");

            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
