namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines how a simulation scenario selects its target devices.
/// </summary>
public enum ScenarioTargetMode
{
    /// <summary>
    /// Applies the scenario to every compatible device.
    /// </summary>
    All,

    /// <summary>
    /// Selects the configured number of compatible devices using the
    /// simulation plan's random source.
    /// </summary>
    RandomCompatible,

    /// <summary>
    /// Applies the scenario to a specific device identified by its device code.
    /// </summary>
    SpecificDevice
}
