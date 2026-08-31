using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "telemetry");

            migrationBuilder.CreateTable(
                name: "Measurements",
                schema: "telemetry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NumericValue = table.Column<double>(type: "float", nullable: true),
                    StateValue = table.Column<bool>(type: "bit", nullable: true),
                    MeasuredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Measurements", x => x.Id);
                    table.CheckConstraint("CK_TelemetryMeasurements_HumidityRange", "[Metric] <> 'Humidity'\r\nOR ([NumericValue] >= 0 AND [NumericValue] <= 100)");
                    table.CheckConstraint("CK_TelemetryMeasurements_ValueKind", "(\r\n    [Metric] IN ('Temperature', 'Humidity')\r\n    AND [NumericValue] IS NOT NULL\r\n    AND [StateValue] IS NULL\r\n)\r\nOR\r\n(\r\n    [Metric] IN ('DoorState', 'LightState')\r\n    AND [NumericValue] IS NULL\r\n    AND [StateValue] IS NOT NULL\r\n)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_DeviceId_Metric_MeasuredAtUtc",
                schema: "telemetry",
                table: "Measurements",
                columns: new[] { "DeviceId", "Metric", "MeasuredAtUtc" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Measurements",
                schema: "telemetry");
        }
    }
}
