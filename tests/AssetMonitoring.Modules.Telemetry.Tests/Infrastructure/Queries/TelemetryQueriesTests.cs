using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Queries;
using AssetMonitoring.Modules.Telemetry.Tests.Database;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Queries;

public sealed class TelemetryQueriesTests
{
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid OtherDeviceId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithNullDbContextThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryQueries(null!));
    }

    [Fact]
    public async Task GetHistoryAsyncWithNullQueryThrowsArgumentNullException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            queries.GetHistoryAsync(null!));
    }

    [Fact]
    public async Task GetHistoryAsyncWithEmptyDeviceIdThrowsArgumentException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(Guid.Empty);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.DeviceId), exception.ParamName);
    }

    [Fact]
    public async Task GetHistoryAsyncWithUnsupportedMetricThrowsArgumentOutOfRangeException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            (TelemetryMetric)999);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.Metric), exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task GetHistoryAsyncWithNonUtcFromTimestampThrowsArgumentException(
        DateTimeKind kind)
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var from = DateTime.SpecifyKind(MeasuredAtUtc, kind);
        var query = new TelemetryHistoryQuery(DeviceId, FromUtc: from);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.FromUtc), exception.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task GetHistoryAsyncWithNonUtcToTimestampThrowsArgumentException(
        DateTimeKind kind)
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var to = DateTime.SpecifyKind(MeasuredAtUtc, kind);
        var query = new TelemetryHistoryQuery(DeviceId, ToUtc: to);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.ToUtc), exception.ParamName);
    }

    [Fact]
    public async Task GetHistoryAsyncWithStartAfterEndThrowsArgumentException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            FromUtc: MeasuredAtUtc.AddMinutes(1),
            ToUtc: MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.GetHistoryAsync(query));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetHistoryAsyncWithInvalidPageThrowsArgumentOutOfRangeException(
        int page)
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(DeviceId, Page: page);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.Page), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task GetHistoryAsyncWithInvalidPageSizeThrowsArgumentOutOfRangeException(
        int pageSize)
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(DeviceId, PageSize: pageSize);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => queries.GetHistoryAsync(query));

        Assert.Equal(nameof(query.PageSize), exception.ParamName);
    }

    [Fact]
    public async Task GetHistoryAsyncWhenNothingMatchesReturnsEmptyPage()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(DeviceId, Page: 2, PageSize: 25);

        var result = await queries.GetHistoryAsync(query);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Page);
        Assert.Equal(25, result.PageSize);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncReturnsOnlyRequestedDeviceMeasurements()
    {
        using var database = new SqliteTelemetryDatabase();
        var expected = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            MeasuredAtUtc);
        var otherDeviceMeasurement = CreateNumericMeasurement(
            MeasurementId(2),
            OtherDeviceId,
            TelemetryMetric.Temperature,
            99,
            MeasuredAtUtc);
        await SaveAsync(database, expected, otherDeviceMeasurement);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetHistoryAsync(
            new TelemetryHistoryQuery(DeviceId));

        var item = Assert.Single(result.Items);
        Assert.Equal(expected.Id, item.MeasurementId);
        Assert.Equal(DeviceId, item.DeviceId);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncFiltersByMetric()
    {
        using var database = new SqliteTelemetryDatabase();
        var temperature = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            MeasuredAtUtc);
        var humidity = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Humidity,
            55,
            MeasuredAtUtc);
        await SaveAsync(database, temperature, humidity);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            TelemetryMetric.Humidity);

        var result = await queries.GetHistoryAsync(query);

        var item = Assert.Single(result.Items);
        Assert.Equal(humidity.Id, item.MeasurementId);
        Assert.Equal(TelemetryMetric.Humidity, item.Metric);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncFromFilterIsInclusive()
    {
        using var database = new SqliteTelemetryDatabase();
        var older = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc.AddMinutes(-1));
        var boundary = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc);
        var newer = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(1));
        await SaveAsync(database, older, boundary, newer);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            FromUtc: MeasuredAtUtc);

        var result = await queries.GetHistoryAsync(query);

        Assert.Equal(
            new[] { newer.Id, boundary.Id },
            result.Items.Select(item => item.MeasurementId));
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncToFilterIsInclusive()
    {
        using var database = new SqliteTelemetryDatabase();
        var older = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc.AddMinutes(-1));
        var boundary = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc);
        var newer = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(1));
        await SaveAsync(database, older, boundary, newer);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            ToUtc: MeasuredAtUtc);

        var result = await queries.GetHistoryAsync(query);

        Assert.Equal(
            new[] { boundary.Id, older.Id },
            result.Items.Select(item => item.MeasurementId));
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncUsesDeterministicDescendingOrder()
    {
        using var database = new SqliteTelemetryDatabase();
        var sameTimeLowerId = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            20,
            MeasuredAtUtc);
        var sameTimeHigherId = CreateNumericMeasurement(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.Temperature,
            21,
            MeasuredAtUtc);
        var newest = CreateNumericMeasurement(
            MeasurementId(3),
            DeviceId,
            TelemetryMetric.Temperature,
            22,
            MeasuredAtUtc.AddMinutes(1));
        await SaveAsync(database, sameTimeLowerId, sameTimeHigherId, newest);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetHistoryAsync(
            new TelemetryHistoryQuery(DeviceId));

        Assert.Equal(
            new[] { newest.Id, sameTimeHigherId.Id, sameTimeLowerId.Id },
            result.Items.Select(item => item.MeasurementId));
    }

    [Fact]
    public async Task GetHistoryAsyncAppliesPaginationAfterCountingAllMatches()
    {
        using var database = new SqliteTelemetryDatabase();
        var measurements = Enumerable.Range(1, 5)
            .Select(index => CreateNumericMeasurement(
                MeasurementId(index),
                DeviceId,
                TelemetryMetric.Temperature,
                20 + index,
                MeasuredAtUtc.AddMinutes(index)))
            .ToArray();
        await SaveAsync(database, measurements);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        var query = new TelemetryHistoryQuery(
            DeviceId,
            Page: 2,
            PageSize: 2);

        var result = await queries.GetHistoryAsync(query);

        Assert.Equal(
            new[] { measurements[2].Id, measurements[1].Id },
            result.Items.Select(item => item.MeasurementId));
        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncProjectsNumericAndStateMeasurements()
    {
        using var database = new SqliteTelemetryDatabase();
        var numeric = CreateNumericMeasurement(
            MeasurementId(1),
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            MeasuredAtUtc);
        var state = TelemetryMeasurement.CreateState(
            MeasurementId(2),
            DeviceId,
            TelemetryMetric.DoorState,
            true,
            MeasuredAtUtc.AddMinutes(1));
        await SaveAsync(database, numeric, state);

        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);

        var result = await queries.GetHistoryAsync(
            new TelemetryHistoryQuery(DeviceId));

        Assert.Collection(
            result.Items,
            stateItem =>
            {
                Assert.Equal(state.Id, stateItem.MeasurementId);
                Assert.Equal(TelemetryMetric.DoorState, stateItem.Metric);
                Assert.Null(stateItem.NumericValue);
                Assert.True(stateItem.StateValue);
                Assert.Equal(state.MeasuredAtUtc, stateItem.MeasuredAtUtc);
            },
            numericItem =>
            {
                Assert.Equal(numeric.Id, numericItem.MeasurementId);
                Assert.Equal(TelemetryMetric.Temperature, numericItem.Metric);
                Assert.Equal(21.5, numericItem.NumericValue);
                Assert.Null(numericItem.StateValue);
                Assert.Equal(numeric.MeasuredAtUtc, numericItem.MeasuredAtUtc);
            });
    }

    [Fact]
    public async Task GetHistoryAsyncWithCanceledTokenThrowsOperationCanceledException()
    {
        using var database = new SqliteTelemetryDatabase();
        await using var context = database.CreateDbContext();
        var queries = new TelemetryQueries(context);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.GetHistoryAsync(
                new TelemetryHistoryQuery(DeviceId),
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
        await context.SaveChangesAsync();
    }
}
