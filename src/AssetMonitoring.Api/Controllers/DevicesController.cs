using AssetMonitoring.Modules.DeviceManagement.Application.Activation;
using AssetMonitoring.Modules.DeviceManagement.Application.Devices;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Api.Controllers;

/// <summary>
/// Provides read-only access to registered device information.
/// </summary>
[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly IDeviceQueries _deviceQueries;
    private readonly DeviceActivationService _deviceActivationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevicesController"/> class.
    /// </summary>
    /// <param name="deviceQueries">The read-only Device Management query service.</param>
    /// <param name="deviceActivationService">The service used to activate device monitoring.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="deviceQueries"/> or
    /// <paramref name="deviceActivationService"/> is null.
    /// </exception>
    public DevicesController(IDeviceQueries deviceQueries, DeviceActivationService deviceActivationService)
    {
        ArgumentNullException.ThrowIfNull(deviceQueries);
        ArgumentNullException.ThrowIfNull(deviceActivationService);

        _deviceQueries = deviceQueries;
        _deviceActivationService = deviceActivationService;
    }

    /// <summary>
    /// Retrieves devices, optionally filtered by lifecycle.
    /// </summary>
    /// <param name="lifecycle">The optional lifecycle used to filter the devices.</param>
    /// <param name="cancellationToken">
    /// A token used to cancel the request.
    /// </param>
    /// <returns>The devices matching the requested lifecycle filter.</returns>
    /// <response code="200">Returns the matching devices.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DeviceResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceResponse>>> GetAllAsync([FromQuery] DeviceLifecycle? lifecycle, CancellationToken cancellationToken)
    {
        var devices = await _deviceQueries.GetAllAsync(lifecycle, cancellationToken);

        return Ok(devices);
    }

    /// <summary>
    /// Retrieves a device by its unique catalog code.
    /// </summary>
    /// <param name="code">
    /// The unique catalog code of the device.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous request.
    /// </param>
    /// <returns>
    /// The requested device when found; otherwise, a
    /// <see cref="ProblemDetails"/> response.
    /// </returns>
    /// <response code="200">Returns the requested device.</response>
    /// <response code="404">
    /// No device with the specified catalog code was found.
    /// </response>
    [HttpGet("{code}")]
    [ProducesResponseType<DeviceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceResponse>> GetByCodeAsync([FromRoute] string code, CancellationToken cancellationToken)
    {
        var devices = await _deviceQueries.GetByCodeAsync(code, cancellationToken);

        if (devices is null)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Device not found.",
                detail: $"A device with code '{code}' was not found.",
                instance: HttpContext.Request.Path);
        }

        return Ok(devices);
    }

    /// <summary>
    /// Activates monitoring for the specified device.
    /// </summary>
    /// <param name="deviceId">The identifier of the device to activate.</param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The resulting device lifecycle and whether it was changed.
    /// </returns>
    /// <response code="200">
    /// The device is active. Repeated activation returns
    /// <c>false</c> in the <c>Changed</c> property.
    /// </response>
    /// <response code="404">
    /// A device with the specified identifier was not found.
    /// </response>
    /// <response code="409">
    /// The device cannot be activated in its current lifecycle.
    /// </response>
    [HttpPost("{deviceId:guid}/activate")]
    [ProducesResponseType<DeviceActivationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceActivationResult>> ActivateAsync([FromRoute] Guid deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _deviceActivationService.ActivateAsync(deviceId, cancellationToken);

            if (result is null)
            {
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Device not found.",
                    detail: $"A device with ID '{deviceId}' was not found.",
                    instance: HttpContext.Request.Path);
            }

            return Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Device cannot be activated.",
                detail: exception.Message,
                instance: HttpContext.Request.Path);
        }
    }
}
