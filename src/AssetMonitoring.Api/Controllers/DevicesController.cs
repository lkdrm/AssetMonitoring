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

    /// <summary>
    /// Initializes a new instance of the <see cref="DevicesController"/> class.
    /// </summary>
    /// <param name="deviceQueries">The read-only Device Management query service.</param>
    public DevicesController(IDeviceQueries deviceQueries)
    {
        ArgumentNullException.ThrowIfNull(deviceQueries);

        _deviceQueries = deviceQueries;
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
}
