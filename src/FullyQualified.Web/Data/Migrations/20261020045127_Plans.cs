using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Plans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OverrideMaxDomains",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverrideMaxEndpoints",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverrideMaxUsers",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OverrideReports",
                table: "tenants",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverrideRetentionMonths",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TierKey",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "free");

            migrationBuilder.CreateTable(
                name: "tiers",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    MaxDomains = table.Column<int>(type: "integer", nullable: true),
                    MaxEndpoints = table.Column<int>(type: "integer", nullable: true),
                    MaxUsers = table.Column<int>(type: "integer", nullable: true),
                    ReportsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RetentionMonths = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tiers", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_TierKey",
                table: "tenants",
                column: "TierKey");

            // The four plans, then everything that exists joins one: the management tenant is unlimited, any other tenant starts on Free.
            migrationBuilder.Sql("""
                INSERT INTO tiers ("Key", "Name", "SortOrder", "MaxDomains", "MaxEndpoints", "MaxUsers", "ReportsEnabled", "RetentionMonths", "UpdatedAt") VALUES
                  ('free',     'Free',     1, 3,   10,  2,    false, 1,    now()),
                  ('starter',  'Starter',  2, 25,  100, 10,   true,  12,   now()),
                  ('business', 'Business', 3, 100, 500, 50,   true,  24,   now()),
                  ('custom',   'Custom',   4, NULL, NULL, NULL, true, NULL, now())
                ON CONFLICT ("Key") DO NOTHING
                """);
            migrationBuilder.Sql("""
                UPDATE tenants SET "TierKey" = 'custom' WHERE "IsManagement"
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_tiers_TierKey",
                table: "tenants",
                column: "TierKey",
                principalTable: "tiers",
                principalColumn: "Key",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenants_tiers_TierKey",
                table: "tenants");

            migrationBuilder.DropTable(
                name: "tiers");

            migrationBuilder.DropIndex(
                name: "IX_tenants_TierKey",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideMaxDomains",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideMaxEndpoints",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideMaxUsers",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideReports",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "OverrideRetentionMonths",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "TierKey",
                table: "tenants");
        }
    }
}
