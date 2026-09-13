using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Repositories;
using AssetMonitoring.Modules.Telemetry.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Persistence;

public sealed class TelemetryMeasurementRepositoryTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithNullDbContextThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryMeasurementRepository(null!));
    }

    [Fact]
    public void AddWithNullMeasurementThrowsArgumentNullException()
    {
        using var database = new SqliteTelemetryDatabase();
        using var context = database.CreateDbContext();
        var repository = new TelemetryMeasurementRepository(context);

        Assert.Throws<ArgumentNullException>(() => repository.Add(null!));
    }

    [Fact]
    public async Task ExistsAsyncWhenMeasurementDoesNotExistReturnsFalse()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var repository = new TelemetryMeasurementRepository(context);

        var exists = await repository.ExistsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(exists);
    }

    [Fact]
    public async Task ExistsAsyncWhenMeasurementExistsReturnsTrue()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = CreateMeasurement();

        await using (var writeContext = database.CreateDbContext())
        {
            writeContext.Measurements.Add(measurement);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = database.CreateDbContext();
        var repository = new TelemetryMeasurementRepository(context);

        var exists = await repository.ExistsAsync(measurement.Id, TestContext.Current.CancellationToken);

        Assert.True(exists);
    }

    [Fact]
    public async Task AddAndSaveChangesAsyncPersistsMeasurement()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = CreateMeasurement();

        await using (var context = database.CreateDbContext())
        {
            var repository = new TelemetryMeasurementRepository(context);

            repository.Add(measurement);
            await repository.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = database.CreateDbContext();
        var persistedMeasurement = await readContext.Measurements
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(measurement.Id, persistedMeasurement.Id);
        Assert.Equal(DeviceId, persistedMeasurement.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, persistedMeasurement.Metric);
        Assert.Equal(21.5, persistedMeasurement.NumericValue);
        Assert.Null(persistedMeasurement.StateValue);
        Assert.Equal(MeasuredAtUtc, persistedMeasurement.MeasuredAtUtc);
    }

    [Fact]
    public async Task ExistsAsyncWithCanceledTokenThrowsOperationCanceledException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var repository = new TelemetryMeasurementRepository(context);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ExistsAsync(
                Guid.NewGuid(),
                cancellationTokenSource.Token));
    }

    private static TelemetryMeasurement CreateMeasurement()
    {
        return TelemetryMeasurement.CreateNumeric(
            Guid.NewGuid(),
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            MeasuredAtUtc);
    }
}
