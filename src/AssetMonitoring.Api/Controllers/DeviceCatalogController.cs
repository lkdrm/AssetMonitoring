using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using Microsoft.AspNetCore.Mvc;

namespace AssetMonitoring.Api.Controllers;

[ApiController]
[Route("api/device-catalog")]
public sealed class DeviceCatalogController : ControllerBase
{
    private readonly DeviceCatalogLoader _deviceCatalogLoader;
    private readonly IWebHostEnvironment _environment;

    public DeviceCatalogController(DeviceCatalogLoader deviceCatalogLoader, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(deviceCatalogLoader);
        ArgumentNullException.ThrowIfNull(environment);

        _deviceCatalogLoader = deviceCatalogLoader;
        _environment = environment;
    }

    [HttpGet("validation")]
    public async Task<IActionResult> Validate(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_environment.ContentRootPath, "Configuration", "DeviceCatalog", "device-catalog.json");

        var result = await _deviceCatalogLoader.LoadAsync(path, cancellationToken);

        return Ok(result);
    }
}
