using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullyQualified.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class HeartbeatProcessInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "worker_heartbeats",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "worker_heartbeats",
                type: "text",
                nullable: false,
                defaultValue: "worker");

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "worker_heartbeats",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Host",
                table: "worker_heartbeats");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "worker_heartbeats");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "worker_heartbeats");
        }
    }
}
