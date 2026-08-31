using AssetMonitoring.Api.Controllers;
using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Modules.Telemetry.Tests.Api;

public sealed class TelemetryControllerTests
{
    private static readonly Guid MeasurementId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithNullRecordingServiceThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryController(null!));
    }

    [Fact]
    public async Task RecordAsyncForNewMeasurementReturnsCreatedResultAndMapsRouteDeviceId()
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var controller = CreateController(repository);
        var request = CreateRequest();

        var actionResult = await controller.RecordAsync(
            DeviceId,
            request,
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var result = Assert.IsType<TelemetryRecordingResult>(response.Value);
        var measurement = Assert.Single(repository.AddedMeasurements);
        Assert.Equal(StatusCodes.Status201Created, response.StatusCode);
        Assert.True(result.Recorded);
        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.Equal(DeviceId, measurement.DeviceId);
        Assert.Equal(MeasurementId, measurement.Id);
    }

    [Fact]
    public async Task RecordAsyncForDuplicateMeasurementReturnsOkResultWithBody()
    {
        var repository =
            new FakeTelemetryMeasurementRepository([MeasurementId]);
        var controller = CreateController(repository);

        var actionResult = await controller.RecordAsync(
            DeviceId,
            CreateRequest(),
            CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<TelemetryRecordingResult>(response.Value);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.False(result.Recorded);
        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    private static TelemetryController CreateController(
        FakeTelemetryMeasurementRepository repository)
    {
        var recordingService = new TelemetryRecordingService(repository);
        return new TelemetryController(recordingService);
    }

    private static TelemetryMeasurementRequest CreateRequest()
    {
        return new TelemetryMeasurementRequest(
            MeasurementId,
            TelemetryMetric.Temperature,
            21.5,
            null,
            MeasuredAtUtc);
    }
}
