using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaxAssets : Migration
    {
        /// <summary>
        /// ADR 0011, scope of #13: nothing is deployed, so no data is carried over. The four old limit columns and assets.Kind go, the new
        /// ones start blank (tenant overrides follow the tier) and the seeded tiers get 5 / 25 / 100 / unlimited by name. Any other tier row stays null.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MaxDomains", table: "tiers");
            migrationBuilder.DropColumn(name: "MaxEndpoints", table: "tiers");
            migrationBuilder.DropColumn(name: "OverrideMaxDomains", table: "tenants");
            migrationBuilder.DropColumn(name: "OverrideMaxEndpoints", table: "tenants");
            migrationBuilder.DropColumn(name: "Kind", table: "assets");

            migrationBuilder.AddColumn<int>(name: "MaxAssets", table: "tiers", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "OverrideMaxAssets", table: "tenants", type: "integer", nullable: true);

            migrationBuilder.Sql("""
                UPDATE tiers SET "MaxAssets" = CASE "Key" WHEN 'free' THEN 5 WHEN 'starter' THEN 25 WHEN 'business' THEN 100 ELSE NULL END;
                """);
        }

        /// <summary>Lossy, for development only: the old columns come back with defaults and every asset becomes a domain.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MaxAssets", table: "tiers");
            migrationBuilder.DropColumn(name: "OverrideMaxAssets", table: "tenants");

            migrationBuilder.AddColumn<int>(name: "MaxDomains", table: "tiers", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "MaxEndpoints", table: "tiers", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "OverrideMaxDomains", table: "tenants", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "OverrideMaxEndpoints", table: "tenants", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "Kind", table: "assets", type: "integer", nullable: false, defaultValue: 0);
        }
    }
}
