using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Tenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_TenantId_Email",
                table: "users");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryForUser",
                table: "invites",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alert_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "alert_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SlackWebhook = table.Column<string>(type: "text", nullable: true),
                    WebhookUrl = table.Column<string>(type: "text", nullable: true),
                    EmailTo = table.Column<string>(type: "text", nullable: true),
                    MinStatus = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NotifyRecovery = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyDrift = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsManagement = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_log_TenantId_At",
                table: "alert_log",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_settings_TenantId",
                table: "alert_settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_IsManagement",
                table: "tenants",
                column: "IsManagement",
                unique: true,
                filter: "\"IsManagement\"");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Name",
                table: "tenants",
                column: "Name",
                unique: true);

            // Data written before tenants existed belongs to one tenant: the management tenant, created here if there is anything to adopt.
            migrationBuilder.Sql("""
                INSERT INTO tenants ("Id", "Name", "IsManagement", "CreatedAt")
                SELECT gen_random_uuid(), 'Management', true, now()
                WHERE EXISTS (SELECT 1 FROM users) OR EXISTS (SELECT 1 FROM assets)
                """);
            migrationBuilder.Sql("""UPDATE assets SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE check_results SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE changes SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE certificates SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE jobs SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE users SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE passkeys SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE invites SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
            migrationBuilder.Sql("""UPDATE external_logins SET "TenantId" = (SELECT "Id" FROM tenants WHERE "IsManagement") WHERE "TenantId" = '00000000-0000-0000-0000-000000000000' AND EXISTS (SELECT 1 FROM tenants)""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_log");

            migrationBuilder.DropTable(
                name: "alert_settings");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RecoveryForUser",
                table: "invites");

            migrationBuilder.CreateIndex(
                name: "IX_users_TenantId_Email",
                table: "users",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }
    }
}
