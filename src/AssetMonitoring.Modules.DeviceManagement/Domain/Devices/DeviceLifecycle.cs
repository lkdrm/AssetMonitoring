namespace AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

/// <summary>
/// Defines the business lifecycle of a device.
/// This is separate from its connection status.
/// </summary>
public enum DeviceLifecycle
{
    /// <summary>
    /// The device exists in the catalog but is not being monitored.
    /// </summary>
    Registered,

    /// <summary>
    /// Monitoring is enabled for the device.
    /// </summary>
    Active,

    /// <summary>
    /// The device is no longer used, but its history is preserved.
    /// </summary>
    Retired
}