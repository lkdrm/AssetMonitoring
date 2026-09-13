using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Tests.Support;

namespace AssetMonitoring.Modules.Telemetry.Tests.Service;

public sealed class TelemetryRecordingServiceTests
{
    private static readonly Guid MeasurementId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithNullRepositoryThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryRecordingService(null!));
    }

    [Fact]
    public async Task RecordAsyncWithNullRequestThrowsArgumentNullException()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.RecordAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsyncForExistingMeasurementReturnsNotRecordedWithoutSaving()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var repository = new FakeTelemetryMeasurementRepository([MeasurementId]);
        var service = CreateService(repository);
        var request = CreateNumericRequest();

        var result = await service.RecordAsync(
            request,
            cancellationTokenSource.Token);

        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.False(result.Recorded);
        Assert.Equal(1, repository.ExistsCallCount);
        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastExistsCancellationToken);
    }

    [Theory]
    [InlineData(TelemetryMetric.Temperature)]
    [InlineData(TelemetryMetric.Humidity)]
    public async Task RecordAsyncForNumericMetricCreatesAndSavesMeasurement(
        TelemetryMetric metric)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = CreateNumericRequest(metric);

        var result = await service.RecordAsync(request, TestContext.Current.CancellationToken);

        var measurement = Assert.Single(repository.AddedMeasurements);
        Assert.True(result.Recorded);
        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.Equal(MeasurementId, measurement.Id);
        Assert.Equal(DeviceId, measurement.DeviceId);
        Assert.Equal(metric, measurement.Metric);
        Assert.Equal(42.5, measurement.NumericValue);
        Assert.Null(measurement.StateValue);
        Assert.Equal(MeasuredAtUtc, measurement.MeasuredAtUtc);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(TelemetryMetric.DoorState, true)]
    [InlineData(TelemetryMetric.LightState, false)]
    public async Task RecordAsyncForStateMetricCreatesAndSavesMeasurement(
        TelemetryMetric metric,
        bool stateValue)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = CreateStateRequest(metric, stateValue);

        var result = await service.RecordAsync(request, TestContext.Current.CancellationToken);

        var measurement = Assert.Single(repository.AddedMeasurements);
        Assert.True(result.Recorded);
        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.Equal(metric, measurement.Metric);
        Assert.Null(measurement.NumericValue);
        Assert.Equal(stateValue, measurement.StateValue);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(TelemetryMetric.Temperature)]
    [InlineData(TelemetryMetric.Humidity)]
    public async Task RecordAsyncForNumericMetricWithoutNumericValueThrowsArgumentException(
        TelemetryMetric metric)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            null,
            null,
            MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(TelemetryMetric.Temperature)]
    [InlineData(TelemetryMetric.Humidity)]
    public async Task RecordAsyncForNumericMetricWithStateValueThrowsArgumentException(
        TelemetryMetric metric)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            42.5,
            true,
            MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(TelemetryMetric.DoorState)]
    [InlineData(TelemetryMetric.LightState)]
    public async Task RecordAsyncForStateMetricWithoutStateValueThrowsArgumentException(
        TelemetryMetric metric)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            null,
            null,
            MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(TelemetryMetric.DoorState)]
    [InlineData(TelemetryMetric.LightState)]
    public async Task RecordAsyncForStateMetricWithNumericValueThrowsArgumentException(
        TelemetryMetric metric)
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            42.5,
            false,
            MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncWithUnsupportedMetricThrowsArgumentOutOfRangeException()
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            (TelemetryMetric)999,
            42.5,
            null,
            MeasuredAtUtc);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.RecordAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncPassesCancellationTokenToRepository()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);

        await service.RecordAsync(
            CreateNumericRequest(),
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastExistsCancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastSaveCancellationToken);
    }

    [Fact]
    public async Task RepeatedRequestRecordsMeasurementOnlyOnce()
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var service = CreateService(repository);
        var request = CreateNumericRequest();

        var firstResult = await service.RecordAsync(request, TestContext.Current.CancellationToken);
        var secondResult = await service.RecordAsync(request, TestContext.Current.CancellationToken);

        Assert.True(firstResult.Recorded);
        Assert.False(secondResult.Recorded);
        Assert.Single(repository.AddedMeasurements);
        Assert.Equal(2, repository.ExistsCallCount);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    private static TelemetryRecordingService CreateService(
        FakeTelemetryMeasurementRepository? repository = null)
    {
        return new TelemetryRecordingService(
            repository ?? new FakeTelemetryMeasurementRepository());
    }

    private static TelemetryRecordingRequest CreateNumericRequest(
        TelemetryMetric metric = TelemetryMetric.Temperature)
    {
        return new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            42.5,
            null,
            MeasuredAtUtc);
    }

    private static TelemetryRecordingRequest CreateStateRequest(
        TelemetryMetric metric,
        bool stateValue)
    {
        return new TelemetryRecordingRequest(
            MeasurementId,
            DeviceId,
            metric,
            null,
            stateValue,
            MeasuredAtUtc);
    }
}
