using AssetMonitoring.Api.Controllers;
using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;
using AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;
using AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Queries;
using AssetMonitoring.Modules.Telemetry.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Modules.Telemetry.Tests.Api;

public sealed class TelemetryLatestControllerTests
{
    private static readonly Guid MeasurementId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetLatestAsyncMapsRouteMetricAndCancellationToken()
    {
        var expected = new TelemetryMeasurementResponse(
            MeasurementId,
            DeviceId,
            TelemetryMetric.Temperature,
            21.5,
            null,
            MeasuredAtUtc);
        var queries = new FakeTelemetryQueries(latestResult: expected);
        var controller = CreateController(queries);
        var request = new TelemetryLatestRequest
        {
            Metric = TelemetryMetric.Temperature
        };
        using var cancellationTokenSource = new CancellationTokenSource();

        var actionResult = await controller.GetLatestAsync(
            DeviceId,
            request,
            cancellationTokenSource.Token);

        var response = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Same(expected, response.Value);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(1, queries.GetLatestCallCount);
        var capturedQuery = Assert.IsType<TelemetryLatestQuery>(
            queries.LastLatestQuery);
        Assert.Equal(DeviceId, capturedQuery.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, capturedQuery.Metric);
        Assert.Equal(
            cancellationTokenSource.Token,
            queries.LastLatestCancellationToken);
    }

    [Fact]
    public async Task GetLatestAsyncWhenNothingMatchesReturnsNotFoundProblemDetails()
    {
        var controller = CreateController(new FakeTelemetryQueries());

        var actionResult = await controller.GetLatestAsync(
            DeviceId,
            CreateRequest(),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.Equal("Telemetry measurement not found.", problem.Title);
        Assert.Contains(
            "Temperature",
            Assert.IsType<string>(problem.Detail));
        Assert.Equal(
            $"/api/devices/{DeviceId}/telemetry/latest",
            problem.Instance);
    }

    [Fact]
    public async Task GetLatestAsyncWithMissingMetricReturnsBadRequestProblemDetails()
    {
        var queries = new FakeTelemetryQueries();
        var controller = CreateController(queries);

        var actionResult = await controller.GetLatestAsync(
            DeviceId,
            new TelemetryLatestRequest(),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("Telemetry latest query is invalid.", problem.Title);
        Assert.Contains(
            "Telemetry metric is required.",
            Assert.IsType<string>(problem.Detail));
        Assert.Equal(0, queries.GetLatestCallCount);
    }

    [Fact]
    public async Task GetLatestAsyncWithNullRequestReturnsBadRequestProblemDetails()
    {
        var queries = new FakeTelemetryQueries();
        var controller = CreateController(queries);

        var actionResult = await controller.GetLatestAsync(
            DeviceId,
            null!,
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("Telemetry latest query is invalid.", problem.Title);
        Assert.Equal(0, queries.GetLatestCallCount);
    }

    [Fact]
    public async Task GetLatestAsyncWithInvalidQueryReturnsBadRequestProblemDetails()
    {
        var queries = new FakeTelemetryQueries
        {
            LatestExceptionToThrow = new ArgumentOutOfRangeException(
                "Metric",
                999,
                "Unsupported telemetry metric.")
        };
        var controller = CreateController(queries);

        var actionResult = await controller.GetLatestAsync(
            DeviceId,
            CreateRequest(),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(actionResult.Result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("Telemetry latest query is invalid.", problem.Title);
        Assert.Contains(
            "Unsupported telemetry metric.",
            Assert.IsType<string>(problem.Detail));
    }

    [Fact]
    public async Task GetLatestAsyncWithCanceledTokenPropagatesCancellation()
    {
        var controller = CreateController(new FakeTelemetryQueries());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.GetLatestAsync(
                DeviceId,
                CreateRequest(),
                cancellationTokenSource.Token));
    }

    private static TelemetryController CreateController(
        FakeTelemetryQueries queries)
    {
        var recordingService = new TelemetryRecordingService(
            new FakeTelemetryMeasurementRepository());
        var controller = new TelemetryController(recordingService, queries);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Path =
            $"/api/devices/{DeviceId}/telemetry/latest";

        return controller;
    }

    private static TelemetryLatestRequest CreateRequest()
    {
        return new TelemetryLatestRequest
        {
            Metric = TelemetryMetric.Temperature
        };
    }
}
