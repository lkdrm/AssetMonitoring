namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Identifies a measurement capability reported by the API
/// for a device available to the simulator.
/// </summary>
public enum SimulatorDeviceCapability
{
    /// <summary>
    /// The device can report temperature measurements.
    /// </summary>
    Temperature,

    /// <summary>
    /// The device can report humidity measurements.
    /// </summary>
    Humidity,

    /// <summary>
    /// The device can report whether a door is open or closed.
    /// </summary>
    DoorState,

    /// <summary>
    /// The device can report whether a light is on or off.
    /// </summary>
    LightState
}
