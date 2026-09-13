namespace AssetMonitoring.DeviceSimulator.Configuration;

/// <summary>
/// Defines configuration options for the device simulator.
/// </summary>
public sealed class DeviceSimulatorOptions
{
    /// <summary>
    /// The configuration section containing device simulator options.
    /// </summary>
    public const string SectionName = "DeviceSimulator";

    /// <summary>
    /// Gets or sets the simulation plan name without the JSON file extension.
    /// </summary>
    public string PlanName { get; set; } = "normal-operation";

    /// <summary>
    /// Gets or sets the absolute HTTP or HTTPS base address
    /// of the Asset Monitoring API.
    /// </summary>
    public string ApiBaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the interval between heartbeat requests
    /// sent by each simulated device.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum number of consecutive transient heartbeat
    /// failures allowed before the device heartbeat runner terminates.
    /// </summary>
    public int MaxConsecutiveHeartbeatFailures { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of consecutive retryable failures
    /// before the device telemetry loop stops.
    /// </summary>
    public int MaxConsecutiveTelemetryFailures { get; set; } = 5;

    /// <summary>
    /// Gets or sets the delay between completed telemetry cycles
    /// for an individual device.
    /// </summary>
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the warehouse time zone identifier used
    /// to evaluate door and light schedules.
    /// </summary>
    public string WarehouseTimeZoneId { get; set; } = "Europe/Prague";
}
