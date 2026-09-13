using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Queries;
using AssetMonitoring.Modules.Telemetry.Tests.Database;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Persistence;

public sealed class TelemetryLatestQueriesTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid OtherDeviceId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetLatestAsyncWithNullQueryThrowsArgumentNullException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            queries.GetLatestAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLatestAsyncWithEmptyDeviceIdThrowsArgumentException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryLatestQuery(
            Guid.Empty,
            TelemetryMetric.Temperature);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.GetLatestAsync(query, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(query.DeviceId), exception.ParamName);
    }

    [Fact]
    public async Task GetLatestAsyncWithUnsupportedMetricThrowsArgumentOutOfRangeException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryLatestQuery(
            DeviceId,
            (TelemetryMetric)999);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queries.GetLatestAsync(query, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(query.Metric), exception.ParamName);
    }

    [Fact]
    public async Task GetLatestAsyncWhenNothingMatchesReturnsNull()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryLatestQuery(
            DeviceId,
            TelemetryMetric.Temperature);

        var result = await queries.GetLatestAsync(query, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestAsyncFiltersByDeviceAndMetric()
    {
        using var database = new SqliteTelemetryDatabase();
        var expected = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            MeasuredAtUtc);
        var otherDevice = CreateNumericMeasurement(
            MeasurementId(2),
            OtherDeviceId,
            TelemetryMetric.Temperature,
            99,
            MeasuredAtUtc.AddMinutes(10));
        var otherMetric = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Humidity,
            55,
            MeasuredAtUtc.AddMinutes(10));
        await SaveAsync(database, expected, otherDevice, otherMetric);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryLatestQuery(
            DeviceId,
            TelemetryMetric.Temperature);

        var result = await queries.GetLatestAsync(query, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(expected.Id, result.MeasurementId);
        Assert.Equal(DeviceId, result.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, result.Metric);
    }

    [Fact]
    public async Task GetLatestAsyncReturnsNewestMeasurementByMeasuredTime()
    {
        using var database = new SqliteTelemetryDatabase();
        var oldest = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var newest = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(2));
        var middle = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc.AddMinutes(1));
        await SaveAsync(database, oldest, newest, middle);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetLatestAsync(new TelemetryLatestQuery(
                DeviceId,
                TelemetryMetric.Temperature), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(newest.Id, result.MeasurementId);
        Assert.Equal(22, result.NumericValue);
        Assert.Equal(newest.MeasuredAtUtc, result.MeasuredAtUtc);
    }

    [Fact]
    public async Task GetLatestAsyncUsesDescendingIdForEqualTimestamps()
    {
        using var database = new SqliteTelemetryDatabase();
        var lowerId = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var higherId = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc);
        await SaveAsync(database, lowerId, higherId);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetLatestAsync(new TelemetryLatestQuery(
                DeviceId,
                TelemetryMetric.Temperature), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(higherId.Id, result.MeasurementId);
    }

    [Fact]
    public async Task GetLatestAsyncProjectsNumericMeasurement()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            -12.5,
            MeasuredAtUtc);
        await SaveAsync(database, measurement);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetLatestAsync(new TelemetryLatestQuery(
                DeviceId,
                TelemetryMetric.Temperature), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(measurement.Id, result.MeasurementId);
        Assert.Equal(DeviceId, result.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, result.Metric);
        Assert.Equal(-12.5, result.NumericValue);
        Assert.Null(result.StateValue);
        Assert.Equal(MeasuredAtUtc, result.MeasuredAtUtc);
    }

    [Fact]
    public async Task GetLatestAsyncProjectsStateMeasurement()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurement = TelemetryMeasurement.CreateState(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.DoorState,
            false,
            MeasuredAtUtc);
        await SaveAsync(database, measurement);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetLatestAsync(new TelemetryLatestQuery(
                DeviceId,
                TelemetryMetric.DoorState), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(measurement.Id, result.MeasurementId);
        Assert.Equal(TelemetryMetric.DoorState, result.Metric);
        Assert.Null(result.NumericValue);
        Assert.False(result.StateValue);
        Assert.Equal(MeasuredAtUtc, result.MeasuredAtUtc);
    }

    [Fact]
    public async Task GetLatestAsyncWithCanceledTokenThrowsOperationCanceledException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.GetLatestAsync(
                new TelemetryLatestQuery(
                    DeviceId,
                    TelemetryMetric.Temperature),
                cancellationTokenSource.Token));
    }

    private static TelemetryMeasurement CreateNumericMeasurement(
        Guid measurementId,
        Guid deviceId,
        TelemetryMetric metric,
        double value,
        DateTime measuredAtUtc)
    {
        return TelemetryMeasurement.CreateNumeric(
            measurementId,
            deviceId,
            metric,
            value,
            measuredAtUtc);
    }

    private static Guid MeasurementId(int value)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    }

    private static async Task SaveAsync(
        SqliteTelemetryDatabase database,
        params TelemetryMeasurement[] measurements)
    {
        await using var context = database.CreateDbContext();
        context.Measurements.AddRange(measurements);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
