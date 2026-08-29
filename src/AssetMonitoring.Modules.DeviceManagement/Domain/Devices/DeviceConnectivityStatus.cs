namespace AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

/// <summary>
/// Represents the calculated connection status of a device.
/// </summary>
public enum DeviceConnectivityStatus
{
    /// <summary>
    /// The device has never reported a heartbeat.
    /// </summary>
    NeverConnected,

    /// <summary>
    /// The latest heartbeat is within the configured offline threshold.
    /// </summary>
    Online,

    /// <summary>
    /// The latest heartbeat is older than the configured offline threshold.
    /// </summary>
    Offline
}
