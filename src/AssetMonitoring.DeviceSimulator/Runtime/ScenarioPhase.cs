namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Represents the execution phase of a simulation scenario.
/// </summary>
public enum ScenarioPhase
{
    /// <summary>
    /// The scenario is waiting for its configured start time.
    /// </summary>
    Pending,

    /// <summary>
    /// The scenario is applying its abnormal behavior to the targeted device.
    /// </summary>
    Active,

    /// <summary>
    /// The scenario is gradually restoring normal behavior.
    /// </summary>
    Recovering,

    /// <summary>
    /// The scenario has finished and no longer affects device measurements.
    /// </summary>
    Completed
}
