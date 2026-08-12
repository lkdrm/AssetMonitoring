using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Devices;

/// <summary>
/// Represents device information returned by Device Management queries.
/// </summary>
public sealed class DeviceResponse
{
    /// <summary>
    /// Gets the unique internal identifier of the device.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the unique catalog code used to identify the device,
    /// for example, ENTRANCE-01.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the human-readable device name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the hardware model of the device.
    /// </summary>
    public required string HardwareModel { get; init; }

    /// <summary>
    /// Gets the hardware revision of the device.
    /// </summary>
    public required string HardwareRevision { get; init; }

    /// <summary>
    /// Gets the firmware version currently installed on the device.
    /// </summary>
    public required string FirmwareVersion { get; init; }

    /// <summary>
    /// Gets the physical or logical location of the device.
    /// </summary>
    public required string Location { get; init; }

    /// <summary>
    /// Gets the telemetry types supported by the device.
    /// Capabilities describe what the device can report
    /// but do not contain current telemetry values.
    /// </summary>
    public required IReadOnlyCollection<DeviceCapability> Capabilities { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the device was registered.
    /// </summary>
    public required DateTime RegisteredAtUtc { get; init; }

    /// <summary>
    /// Gets the current business lifecycle of the device.
    /// Lifecycle is independent of its online or offline connection status.
    /// </summary>
    public required DeviceLifecycle Lifecycle { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the device was retired,
    /// or null when it is not retired.
    /// </summary>
    public required DateTime? RetiredAtUtc { get; init; }
}
