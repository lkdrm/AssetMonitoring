namespace AssetMonitoring.Modules.Telemetry.Domain.Measurements;

/// <summary>
/// Identifies the type of telemetry reported by a device.
/// </summary>
public enum TelemetryMetric
{
    /// <summary>
    /// A numeric temperature measurement.
    /// </summary>
    Temperature,

    /// <summary>
    /// A numeric relative-humidity measurement expressed as a percentage.
    /// </summary>
    Humidity,

    /// <summary>
    /// A boolean measurement representing whether a door is open or closed.
    /// </summary>
    DoorState,

    /// <summary>
    /// A boolean measurement representing whether a light is on or off.
    /// </summary>
    LightState
}
