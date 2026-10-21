namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines a scheduled outage that temporarily suspends heartbeat
/// and telemetry sending for selected devices.
/// </summary>
/// <param name="Name">
/// The unique scenario name within the simulation plan.
/// </param>
/// <param name="StartsAfter">
/// The delay from simulation startup until the outage begins.
/// </param>
/// <param name="Duration">
/// The amount of simulation time for which sending remains suspended.
/// </param>
/// <param name="Target">
/// The configuration used to select affected active devices.
/// No specific telemetry capability is required.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether sending resumes automatically after the outage.
/// Currently, only automatic recovery is supported.
/// </param>
/// <param name="RecoveryDuration">
/// Must be null because sending resumes without a gradual recovery phase.
/// </param>
public sealed record class DeviceOfflineScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration)
: ScenarioDefinition(Name, StartsAfter, Duration, Target, AutoRecover, RecoveryDuration);
