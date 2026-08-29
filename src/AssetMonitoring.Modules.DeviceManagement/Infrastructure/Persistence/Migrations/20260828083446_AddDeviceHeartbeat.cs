using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetMonitoring.Modules.DeviceManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAtUtc",
                schema: "device_management",
                table: "Devices",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastHeartbeatAtUtc",
                schema: "device_management",
                table: "Devices");
        }
    }
}