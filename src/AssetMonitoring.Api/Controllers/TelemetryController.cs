using AssetMonitoring.Api.Contracts.Telemetry;
using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;
using AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Api.Controllers;

/// <summary>
/// Provides HTTP operations for recording device telemetry measurements.
/// </summary>
[ApiController]
[Route("api/devices/{deviceId:guid}/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly TelemetryRecordingService _recordingService;
    private readonly ITelemetryQueries _telemetryQueries;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryController"/> class.
    /// </summary>
    /// <param name="recordingService">
    /// The application service used to record telemetry measurements.
    /// </param>
    /// <param name="telemetryQueries">
    /// The read-only service used to query telemetry measurement history.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="recordingService"/> is null.
    /// </exception>
    public TelemetryController(TelemetryRecordingService recordingService, ITelemetryQueries telemetryQueries)
    {
        ArgumentNullException.ThrowIfNull(recordingService);
        ArgumentNullException.ThrowIfNull(telemetryQueries);

        _recordingService = recordingService;
        _telemetryQueries = telemetryQueries;
    }

    /// <summary>
    /// Records a telemetry measurement produced by the specified device.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device that produced the measurement.
    /// </param>
    /// <param name="request">
    /// The telemetry measurement HTTP request.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The telemetry recording result or a validation problem response.
    /// </returns>
    /// <response code="201">
    /// A new telemetry measurement was persisted.
    /// </response>
    /// <response code="200">
    /// The measurement already existed and was handled idempotently.
    /// </response>
    /// <response code="400">
    /// The telemetry measurement request was invalid.
    /// </response>
    [HttpPost]
    [ProducesResponseType<TelemetryRecordingResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<TelemetryRecordingResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TelemetryRecordingResult>> RecordAsync([FromRoute] Guid deviceId, [FromBody] TelemetryMeasurementRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var measurement = new TelemetryRecordingRequest(request.MeasurementId, deviceId, request.Metric, request.NumericValue, request.StateValue, request.MeasuredAtUtc);

            var result = await _recordingService.RecordAsync(measurement, cancellationToken);

            if (!result.Recorded)
            {
                return Ok(result);
            }

            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Telemetry measurement cannot be recorded.",
                detail: exception.Message,
                instance: HttpContext.Request.Path);
        }
    }

    /// <summary>
    /// Retrieves filtered and paginated telemetry history for a device.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device whose telemetry history is requested.
    /// </param>
    /// <param name="request">
    /// The optional telemetry filters and pagination parameters.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// A page of telemetry measurements matching the supplied filters.
    /// </returns>
    /// <response code="200">
    /// The telemetry history was retrieved successfully. The collection may be
    /// empty when no measurements match the query.
    /// </response>
    /// <response code="400">
    /// The device identifier, filters, time range, or pagination is invalid.
    /// </response>
    [HttpGet]
    [ProducesResponseType<TelemetryHistoryResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TelemetryHistoryResult>> GetHistoryAsync([FromRoute] Guid deviceId, [FromQuery] TelemetryHistoryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);

            var historyQuery = new TelemetryHistoryQuery(deviceId, request.Metric, request.FromUtc, request.ToUtc, request.Page, request.PageSize);

            var result = await _telemetryQueries.GetHistoryAsync(historyQuery, cancellationToken);

            return Ok(result);

        }
        catch (ArgumentException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Telemetry history query is invalid.",
                detail: exception.Message,
                instance: HttpContext.Request.Path);
        }
    }

    /// <summary>
    /// Retrieves the latest telemetry measurement for a device and metric.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device whose latest measurement is requested.
    /// </param>
    /// <param name="request">
    /// The required telemetry metric query parameter.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The latest matching telemetry measurement or a Problem Details response.
    /// </returns>
    /// <response code="200">
    /// The latest matching telemetry measurement was found.
    /// </response>
    /// <response code="400">
    /// The device identifier or telemetry metric is invalid.
    /// </response>
    /// <response code="404">
    /// No matching telemetry measurement exists.
    /// </response>
    [HttpGet("latest")]
    [ProducesResponseType<TelemetryMeasurementResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TelemetryMeasurementResponse>> GetLatestAsync([FromRoute] Guid deviceId, [FromQuery] TelemetryLatestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!request.Metric.HasValue)
            {
                throw new ArgumentException("Telemetry metric is required.", nameof(request.Metric));
            }

            var latestQuery = new TelemetryLatestQuery(deviceId, request.Metric.Value);

            var result = await _telemetryQueries.GetLatestAsync(latestQuery, cancellationToken);

            if (result is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Telemetry measurement not found.",
                    detail:
                        $"No '{request.Metric.Value}' telemetry measurement " +
                        $"was found for device '{deviceId}'.",
                    instance: HttpContext.Request.Path);
            }

            return Ok(result);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Telemetry latest query is invalid.",
                detail: exception.Message,
                instance: HttpContext.Request.Path);
        }
    }
}
