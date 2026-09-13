namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Identifies the type of telemetry measurement sent to the API.
/// </summary>
public enum SimulatorTelemetryMetric
{
    /// <summary>
    /// A numeric temperature measurement.
    /// </summary>
    Temperature,

    /// <summary>
    /// A numeric relative humidity measurement expressed as a percentage.
    /// </summary>
    Humidity,

    /// <summary>
    /// A boolean measurement indicating whether a door is open.
    /// </summary>
    DoorState,

    /// <summary>
    /// A boolean measurement indicating whether a light is on.
    /// </summary>
    LightState
}
