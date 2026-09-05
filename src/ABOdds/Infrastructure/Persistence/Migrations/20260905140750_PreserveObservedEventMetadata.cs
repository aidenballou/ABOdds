using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABOdds.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveObservedEventMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventMetadataJson",
                table: "poll_batches",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalculationVersion",
                table: "fair_values",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy-unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventMetadataJson",
                table: "poll_batches");

            migrationBuilder.DropColumn(
                name: "CalculationVersion",
                table: "fair_values");
        }
    }
}
