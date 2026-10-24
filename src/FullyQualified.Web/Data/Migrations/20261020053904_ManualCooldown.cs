using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualCooldown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ManualCooldownMinutes",
                table: "tiers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverrideManualCooldownMinutes",
                table: "tenants",
                type: "integer",
                nullable: true);

            // Free waits an hour between uses, Starter 15 minutes, Business 5. Custom has none and follows the server default.
            migrationBuilder.Sql("""
                UPDATE tiers SET "ManualCooldownMinutes" = CASE "Key" WHEN 'free' THEN 60 WHEN 'starter' THEN 15 WHEN 'business' THEN 5 ELSE NULL END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualCooldownMinutes",
                table: "tiers");

            migrationBuilder.DropColumn(
                name: "OverrideManualCooldownMinutes",
                table: "tenants");
        }
    }
}
