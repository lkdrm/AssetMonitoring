using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Api.Controllers;

/// <summary>
/// Provides operations for loading and validating the device catalog.
/// </summary>
[ApiController]
[Route("api/device-catalog")]
public sealed class DeviceCatalogController : ControllerBase
{
    private readonly DeviceCatalogLoader _deviceCatalogLoader;
    private readonly IWebHostEnvironment _environment;
    private readonly DeviceCatalogSynchronizationService _synchronizationService;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceCatalogController"/> class.
    /// </summary>
    /// <param name="deviceCatalogLoader">The service used to load and validate the device catalog.</param>
    /// <param name="environment">Information about the API hosting environment.</param>
    public DeviceCatalogController(DeviceCatalogLoader deviceCatalogLoader, IWebHostEnvironment environment, DeviceCatalogSynchronizationService synchronizationService)
    {
        ArgumentNullException.ThrowIfNull(deviceCatalogLoader);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(synchronizationService);

        _deviceCatalogLoader = deviceCatalogLoader;
        _environment = environment;
        _synchronizationService = synchronizationService;
    }

    /// <summary>
    /// Reads and validates the configured device catalog.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The loaded device catalog together with its validation result.</returns>
    [HttpGet("validation")]
    public async Task<ActionResult<DeviceCatalogLoadResult>> Validate(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_environment.ContentRootPath, "Configuration", "DeviceCatalog", "device-catalog.json");

        var result = await _deviceCatalogLoader.LoadAsync(path, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Synchronizes the Device Management database with the configured
    /// JSON device catalog.
    /// </summary>
    /// <remarks>
    /// Creates new devices, updates existing metadata, restores retired devices
    /// that reappear in the catalog, and retires devices missing from the catalog.
    /// No database changes are applied when catalog validation fails.
    /// </remarks>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous synchronization operation.
    /// </param>
    /// <returns>
    /// An action result containing catalog validation information and the numbers
    /// of created, updated, unchanged, retired, and restored devices.
    /// </returns>
    /// <response code="200">The device catalog was successfully synchronized.</response>
    /// <response code="400">The device catalog failed validation.</response>
    [HttpPost("synchronize")]
    public async Task<ActionResult<DeviceCatalogSynchronizationResult>> Synchronize(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_environment.ContentRootPath, "Configuration", "DeviceCatalog", "device-catalog.json");

        var result = await _synchronizationService.SynchronizeAsync(path, cancellationToken);
        if (!result.ValidationResult.IsValid)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
