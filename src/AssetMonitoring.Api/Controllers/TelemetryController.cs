using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Application.Service;
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

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryController"/> class.
    /// </summary>
    /// <param name="recordingService">
    /// The application service used to record telemetry measurements.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="recordingService"/> is null.
    /// </exception>
    public TelemetryController(TelemetryRecordingService recordingService)
    {
        ArgumentNullException.ThrowIfNull(recordingService);

        _recordingService = recordingService;
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
}
