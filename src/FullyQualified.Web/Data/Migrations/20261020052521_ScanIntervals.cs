using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScanIntervalMinutes",
                table: "tiers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastManualRunAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverrideScanIntervalMinutes",
                table: "tenants",
                type: "integer",
                nullable: true);

            // Free checks daily, Starter every six hours, Business hourly. Custom has none and follows the server default.
            migrationBuilder.Sql("""
                UPDATE tiers SET "ScanIntervalMinutes" = CASE "Key" WHEN 'free' THEN 1440 WHEN 'starter' THEN 360 WHEN 'business' THEN 60 ELSE NULL END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScanIntervalMinutes",
                table: "tiers");

            migrationBuilder.DropColumn(
                name: "LastManualRunAt",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideScanIntervalMinutes",
                table: "tenants");
        }
    }
}
