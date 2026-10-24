using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DiscoveredCertExpires",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DiscoveredFromId",
                table: "assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveredIssuer",
                table: "assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveredVia",
                table: "assets",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveredCertExpires",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DiscoveredFromId",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DiscoveredIssuer",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "DiscoveredVia",
                table: "assets");
        }
    }
}
