using AssetMonitoring.Api.Contracts.Telemetry;
using AssetMonitoring.Api.Controllers;
using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;
using AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Queries;
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
        new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithNullRecordingServiceThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryController(null!, new FakeTelemetryQueries()));
    }

    [Fact]
    public void ConstructorWithNullTelemetryQueriesThrowsArgumentNullException()
    {
        var recordingService = new TelemetryRecordingService(
            new FakeTelemetryMeasurementRepository());

        Assert.Throws<ArgumentNullException>(() =>
            new TelemetryController(recordingService, null!));
    }

    [Fact]
    public async Task RecordAsyncForNewMeasurementReturnsCreatedResultAndMapsRouteDeviceId()
    {
        var repository = new FakeTelemetryMeasurementRepository();
        var controller = CreateController(repository);
        var request = CreateRecordingRequest();

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
            CreateRecordingRequest(),
            CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<TelemetryRecordingResult>(response.Value);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.False(result.Recorded);
        Assert.Equal(MeasurementId, result.MeasurementId);
        Assert.Empty(repository.AddedMeasurements);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task GetHistoryAsyncMapsRouteFiltersPaginationAndCancellationToken()
    {
        var expectedItem = new TelemetryMeasurementResponse(
            MeasurementId,
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            null,
            MeasuredAtUtc);
        var expectedResult = new TelemetryHistoryResult(
            [expectedItem],
            2,
            25,
            30);
        var queries = new FakeTelemetryQueries(expectedResult);
        var controller = CreateController(
            new FakeTelemetryMeasurementRepository(),
            queries);
        var fromUtc = MeasuredAtUtc.AddHours(-1);
        var toUtc = MeasuredAtUtc.AddHours(1);
        var request = new TelemetryHistoryRequest
        {
            Metric = TelemetryMetric.Temperature,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Page = 2,
            PageSize = 25
        };
        using var cancellationTokenSource = new CancellationTokenSource();

        var actionResult = await controller.GetHistoryAsync(
            DeviceId,
            request,
            cancellationTokenSource.Token);

        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Same(expectedResult, response.Value);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(1, queries.GetHistoryCallCount);
        var capturedQuery = Assert.IsType<TelemetryHistoryQuery>(
            queries.LastQuery);
        Assert.Equal(DeviceId, capturedQuery.DeviceId);
        Assert.Equal(request.Metric, capturedQuery.Metric);
        Assert.Equal(fromUtc, capturedQuery.FromUtc);
        Assert.Equal(toUtc, capturedQuery.ToUtc);
        Assert.Equal(2, capturedQuery.Page);
        Assert.Equal(25, capturedQuery.PageSize);
        Assert.Equal(
            cancellationTokenSource.Token,
            queries.LastCancellationToken);
    }

    [Fact]
    public async Task GetHistoryAsyncForEmptyHistoryReturnsOkWithEmptyResult()
    {
        var expectedResult = new TelemetryHistoryResult([], 1, 50, 0);
        var controller = CreateController(
            new FakeTelemetryMeasurementRepository(),
            new FakeTelemetryQueries(expectedResult));

        var actionResult = await controller.GetHistoryAsync(
            DeviceId,
            new TelemetryHistoryRequest(),
            CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<TelemetryHistoryResult>(response.Value);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task GetHistoryAsyncWithNullRequestReturnsBadRequestProblemDetails()
    {
        var controller = CreateController(
            new FakeTelemetryMeasurementRepository());

        var actionResult = await controller.GetHistoryAsync(
            DeviceId,
            null!,
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(
            "Telemetry history query is invalid.",
            problem.Title);
        Assert.Equal(
            $"/api/devices/{DeviceId}/telemetry",
            problem.Instance);
    }

    [Fact]
    public async Task GetHistoryAsyncWithInvalidQueryReturnsBadRequestProblemDetails()
    {
        var queries = new FakeTelemetryQueries
        {
            ExceptionToThrow = new ArgumentOutOfRangeException(
                "Page",
                0,
                "Page number must be at least 1.")
        };
        var controller = CreateController(
            new FakeTelemetryMeasurementRepository(),
            queries);

        var actionResult = await controller.GetHistoryAsync(
            DeviceId,
            new TelemetryHistoryRequest { Page = 0 },
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(
            "Telemetry history query is invalid.",
            problem.Title);
        Assert.Contains(
            "Page number must be at least 1.",
            Assert.IsType<string>(problem.Detail));
    }

    [Fact]
    public async Task GetHistoryAsyncWithCanceledTokenPropagatesCancellation()
    {
        var controller = CreateController(
            new FakeTelemetryMeasurementRepository());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.GetHistoryAsync(
                DeviceId,
                new TelemetryHistoryRequest(),
                cancellationTokenSource.Token));
    }

    private static TelemetryController CreateController(
        FakeTelemetryMeasurementRepository repository,
        FakeTelemetryQueries? queries = null)
    {
        var recordingService = new TelemetryRecordingService(repository);
        var controller = new TelemetryController(
            recordingService,
            queries ?? new FakeTelemetryQueries());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Path =
            $"/api/devices/{DeviceId}/telemetry";

        return controller;
    }

    private static TelemetryMeasurementRequest CreateRecordingRequest()
    {
        return new TelemetryMeasurementRequest(
            MeasurementId,
            TelemetryMetric.Temperature,
            21.5,
            null,
            MeasuredAtUtc);
    }
}
