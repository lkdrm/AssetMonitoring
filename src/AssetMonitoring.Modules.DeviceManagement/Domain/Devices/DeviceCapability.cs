namespace AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

/// <summary>
/// Defines telemetry types that a device can provide.
/// </summary>
public enum DeviceCapability
{
    /// <summary>
    /// Measures temperature.
    /// </summary>
    Temperature,

    /// <summary>
    /// Measures relative humidity.
    /// </summary>
    Humidity,

    /// <summary>
    /// Reports whether a door is open or closed.
    /// </summary>
    DoorState,

    /// <summary>
    /// Reports whether a light is on or off.
    /// </summary>
    LightState
}