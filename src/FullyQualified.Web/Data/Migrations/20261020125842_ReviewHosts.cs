using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewHosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Service = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CertExpires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_hosts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_hosts_TenantId_Name",
                table: "review_hosts",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_hosts_TenantId_State",
                table: "review_hosts",
                columns: new[] { "TenantId", "State" });

            // Hostnames that were added automatically before there was a review step count as added, so the review list and the reports are complete.
            migrationBuilder.Sql("""
                INSERT INTO review_hosts ("Id", "TenantId", "ParentAssetId", "Name", "FirstSeenAt", "LastSeenAt", "Service", "Issuer", "CertExpires", "State", "Reason", "DecidedAt", "DecidedBy")
                SELECT gen_random_uuid(), "TenantId", "DiscoveredFromId", "Name", "CreatedAt", "CreatedAt", "DiscoveredVia", "DiscoveredIssuer", "DiscoveredCertExpires", 1, NULL, "CreatedAt", NULL
                FROM assets WHERE "Discovered" = true
                ON CONFLICT DO NOTHING
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_hosts");
        }
    }
}
