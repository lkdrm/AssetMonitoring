namespace AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

/// <summary>
/// Defines telemetry types that a device can provide.
/// </summary>
public enum DeviceCapability
{
    /// <summary>
    /// Measures temperature.
    /// </summary>
    Temperature = 0,

    /// <summary>
    /// Measures relative humidity.
    /// </summary>
    Humidity = 1,

    /// <summary>
    /// Reports whether a door is open or closed.
    /// </summary>
    DoorState = 2,

    /// <summary>
    /// Reports whether a light is on or off.
    /// </summary>
    LightState = 3
}