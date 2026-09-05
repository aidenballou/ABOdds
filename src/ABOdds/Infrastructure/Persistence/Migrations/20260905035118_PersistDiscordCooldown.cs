using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABOdds.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistDiscordCooldown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RateLimitedUntilUtc",
                table: "alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_alerts_RateLimitedUntilUtc",
                table: "alerts",
                column: "RateLimitedUntilUtc",
                filter: "\"RateLimitedUntilUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_alerts_RateLimitedUntilUtc",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "RateLimitedUntilUtc",
                table: "alerts");
        }
    }
}
