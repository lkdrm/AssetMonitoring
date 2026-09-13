using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Tests.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetMonitoring.Modules.Telemetry.Tests;

public sealed class TelemetryPersistenceTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ModelContainsExpectedTelemetryMeasurementMapping()
    {
        using var database = new SqliteTelemetryDatabase();
        using var context = database.CreateDbContext();

        var model = context.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(TelemetryMeasurement));

        Assert.NotNull(entityType);
        Assert.Equal("Measurements", entityType.GetTableName());
        Assert.Equal("telemetry", entityType.GetSchema());

        var idProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.Id));
        Assert.NotNull(idProperty);
        Assert.Equal(ValueGenerated.Never, idProperty.ValueGenerated);

        var deviceIdProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.DeviceId));
        var metricProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.Metric));
        var numericProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.NumericValue));
        var stateProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.StateValue));
        var measuredAtProperty = entityType.FindProperty(
            nameof(TelemetryMeasurement.MeasuredAtUtc));

        Assert.NotNull(deviceIdProperty);
        Assert.NotNull(metricProperty);
        Assert.NotNull(numericProperty);
        Assert.NotNull(stateProperty);
        Assert.NotNull(measuredAtProperty);
        Assert.False(deviceIdProperty.IsNullable);
        Assert.False(metricProperty.IsNullable);
        Assert.Equal(50, metricProperty.GetMaxLength());
        Assert.True(numericProperty.IsNullable);
        Assert.True(stateProperty.IsNullable);
        Assert.False(measuredAtProperty.IsNullable);
        Assert.Equal("datetime2", measuredAtProperty.GetColumnType());

        Assert.Contains(
            entityType.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_TelemetryMeasurements_ValueKind");
        Assert.Contains(
            entityType.GetCheckConstraints(),
            constraint =>
                constraint.Name ==
                "CK_TelemetryMeasurements_HumidityRange");

        var historyIndex = Assert.Single(entityType.GetIndexes());
        Assert.Equal(
            new[]
            {
                nameof(TelemetryMeasurement.DeviceId),
                nameof(TelemetryMeasurement.Metric),
                nameof(TelemetryMeasurement.MeasuredAtUtc)
            },
            historyIndex.Properties.Select(property => property.Name));
        Assert.Equal(
            "IX_Measurements_DeviceId_Metric_MeasuredAtUtc",
            historyIndex.GetDatabaseName());
    }

    [Fact]
    public async Task SaveAndReloadPreservesNumericMeasurementAndUtcTimestamp()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = TelemetryMeasurement.CreateNumeric(
            Guid.NewGuid(),
            DeviceId,
            TelemetryMetric.Temperature,
            -12.5,
            MeasuredAtUtc);

        await SaveAsync(database, measurement);

        await using var readContext = database.CreateDbContext();
        var loadedMeasurement = await readContext.Measurements
            .AsNoTracking()
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(measurement.Id, loadedMeasurement.Id);
        Assert.Equal(DeviceId, loadedMeasurement.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, loadedMeasurement.Metric);
        Assert.Equal(-12.5, loadedMeasurement.NumericValue);
        Assert.Null(loadedMeasurement.StateValue);
        Assert.Equal(MeasuredAtUtc, loadedMeasurement.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, loadedMeasurement.MeasuredAtUtc.Kind);
    }

    [Fact]
    public async Task SaveAndReloadPreservesFalseStateMeasurement()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = TelemetryMeasurement.CreateState(
            Guid.NewGuid(),
            DeviceId,
            TelemetryMetric.LightState,
            false,
            MeasuredAtUtc);

        await SaveAsync(database, measurement);

        await using var readContext = database.CreateDbContext();
        var loadedMeasurement = await readContext.Measurements
            .AsNoTracking()
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TelemetryMetric.LightState, loadedMeasurement.Metric);
        Assert.Null(loadedMeasurement.NumericValue);
        Assert.False(loadedMeasurement.StateValue);
        Assert.Equal(MeasuredAtUtc, loadedMeasurement.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, loadedMeasurement.MeasuredAtUtc.Kind);
    }

    [Fact]
    public async Task DuplicateMeasurementIdIsRejectedByDatabase()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurementId = Guid.NewGuid();
        var firstMeasurement = TelemetryMeasurement.CreateNumeric(
            measurementId,
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var duplicateMeasurement = TelemetryMeasurement.CreateNumeric(
            measurementId,
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc.AddMinutes(1));

        await SaveAsync(database, firstMeasurement);

        await using var duplicateContext = database.CreateDbContext();
        duplicateContext.Measurements.Add(duplicateMeasurement);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseRejectsMetricWithWrongValueKind()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Measurements"
                    ("Id", "DeviceId", "Metric", "NumericValue",
                     "StateValue", "MeasuredAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {DeviceId}, {"DoorState"},
                     {1.0}, {true}, {MeasuredAtUtc})
                """, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseRejectsHumidityOutsideValidRange()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();

        await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Measurements"
                    ("Id", "DeviceId", "Metric", "NumericValue",
                     "StateValue", "MeasuredAtUtc")
                VALUES
                    ({Guid.NewGuid()}, {DeviceId}, {"Humidity"},
                     {100.1}, NULL, {MeasuredAtUtc})
                """, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutOfOrderMeasurementsAreStoredAndOrderedByMeasuredTime()
    {
        using var database = new SqliteTelemetryDatabase();
        var olderMeasurement = TelemetryMeasurement.CreateNumeric(
            Guid.NewGuid(),
            DeviceId,
            TelemetryMetric.Temperature,
            19,
            MeasuredAtUtc);
        var newerMeasurement = TelemetryMeasurement.CreateNumeric(
            Guid.NewGuid(),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc.AddMinutes(1));

        await using (var writeContext = database.CreateDbContext())
        {
            writeContext.Measurements.Add(newerMeasurement);
            writeContext.Measurements.Add(olderMeasurement);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateDbContext();
        var measurementIds = await readContext.Measurements
            .AsNoTracking()
            .OrderByDescending(measurement => measurement.MeasuredAtUtc)
            .Select(measurement => measurement.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { newerMeasurement.Id, olderMeasurement.Id },
            measurementIds);
    }

    private static async Task SaveAsync(
        SqliteTelemetryDatabase database,
        TelemetryMeasurement measurement)
    {
        await using var context = database.CreateDbContext();
        context.Measurements.Add(measurement);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
