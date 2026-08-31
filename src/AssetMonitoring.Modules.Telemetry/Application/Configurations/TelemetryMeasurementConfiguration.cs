using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetMonitoring.Modules.Telemetry.Application.Configurations;

internal sealed class TelemetryMeasurementConfiguration : IEntityTypeConfiguration<TelemetryMeasurement>
{
    public void Configure(EntityTypeBuilder<TelemetryMeasurement> builder)
    {
        builder.ToTable("Measurements", "telemetry",
            tableBuilder =>
            {
                tableBuilder.HasCheckConstraint(
                    "CK_TelemetryMeasurements_ValueKind",
                    """
                    (
                        [Metric] IN ('Temperature', 'Humidity')
                        AND [NumericValue] IS NOT NULL
                        AND [StateValue] IS NULL
                    )
                    OR
                    (
                        [Metric] IN ('DoorState', 'LightState')
                        AND [NumericValue] IS NULL
                        AND [StateValue] IS NOT NULL
                    )
                    """);

                tableBuilder.HasCheckConstraint(
                    "CK_TelemetryMeasurements_HumidityRange",
                    """
                    [Metric] <> 'Humidity'
                    OR ([NumericValue] >= 0 AND [NumericValue] <= 100)
                    """);
            });

        builder.HasKey(measurement => measurement.Id);

        // The identifier comes from the device message and is also used
        // to detect duplicate message delivery.
        builder.Property(measurement => measurement.Id)
            .ValueGeneratedNever();

        builder.Property(measurement => measurement.DeviceId)
            .IsRequired();

        builder.Property(measurement => measurement.Metric)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(measurement => measurement.NumericValue)
            .IsRequired(false);

        builder.Property(measurement => measurement.StateValue)
            .IsRequired(false);

        // SQL Server datetime2 does not preserve DateTimeKind.
        builder.Property(measurement => measurement.MeasuredAtUtc)
            .HasColumnType("datetime2")
            .HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .IsRequired();

        // Supports measurement-history and latest-value queries.
        builder.HasIndex(
                measurement => new
                {
                    measurement.DeviceId,
                    measurement.Metric,
                    measurement.MeasuredAtUtc
                })
            .HasDatabaseName(
                "IX_Measurements_DeviceId_Metric_MeasuredAtUtc")
            .IsDescending(false, false, true);
    }
}
