namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Identifies the lifecycle state reported by the API for a device.
/// </summary>
public enum SimulatorDeviceLifecycle
{
    /// <summary>
    /// The device is registered and requires activation before simulation.
    /// </summary>
    Registered,

    /// <summary>
    /// The device is active and eligible for simulation.
    /// </summary>
    Active,

    /// <summary>
    /// The device is retired and must be excluded from simulation.
    /// </summary>
    Retired
}
