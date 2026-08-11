using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

/// <summary>
/// Represents a single raw device definition loaded from the device catalog.
/// </summary>
/// <remarks>
/// The item must be validated before it is converted into a domain
/// <see cref="Device"/> entity.
/// </remarks>
public sealed class DeviceCatalogItem
{
    /// <summary>
    /// Gets the unique catalog code of the device.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the human-readable device name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the hardware model.
    /// </summary>
    public required string HardwareModel { get; init; }

    /// <summary>
    /// Gets the hardware revision.
    /// </summary>
    public required string HardwareRevision { get; init; }

    /// <summary>
    /// Gets the installed firmware version.
    /// </summary>
    public required string FirmwareVersion { get; init; }

    /// <summary>
    /// Gets the physical location of the device.
    /// </summary>
    public required string Location { get; init; }

    /// <summary>
    /// Gets the telemetry capabilities declared for the device.
    /// The list preserves the original catalog values for validation.
    /// </summary>
    public required List<DeviceCapability> Capabilities { get; init; }
}
