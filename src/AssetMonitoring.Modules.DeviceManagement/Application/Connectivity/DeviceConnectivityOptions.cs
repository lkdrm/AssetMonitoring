namespace AssetMonitoring.Modules.DeviceManagement.Application.Connectivity;

/// <summary>
/// Defines configuration used to calculate device connectivity.
/// </summary>
public sealed class DeviceConnectivityOptions
{
    /// <summary>
    /// Gets the configuration section name used to bind connectivity options.
    /// </summary>
    public const string SectionName = "DeviceManagement:Connectivity";

    /// <summary>
    /// Gets or sets the maximum permitted time since the latest heartbeat
    /// before a device is considered offline.
    /// </summary>
    public TimeSpan OfflineThreshold { get; set; } = TimeSpan.FromMinutes(5);
}
