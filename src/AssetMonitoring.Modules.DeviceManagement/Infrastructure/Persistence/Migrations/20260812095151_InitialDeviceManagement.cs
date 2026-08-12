using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetMonitoring.Modules.DeviceManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDeviceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "device_management");

            migrationBuilder.CreateTable(
                name: "Devices",
                schema: "device_management",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HardwareModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HardwareRevision = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Lifecycle = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Capabilities = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Code",
                schema: "device_management",
                table: "Devices",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices",
                schema: "device_management");
        }
    }
}
