namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines a scenario that keeps the lights associated with each targeted device
/// on for a configured active duration.
/// </summary>
/// <remarks>
/// Before activation and after automatic completion, light measurements
/// follow normal generation based on the warehouse schedule.
/// Automatic recovery restores normal behavior immediately.
/// </remarks>
/// <param name="Name">
/// The unique human-readable scenario name.
/// </param>
/// <param name="StartsAfter">
/// The earliest elapsed simulation time at which the scenario may begin.
/// A preceding scenario for the same device and metric can delay
/// the actual start.
/// </param>
/// <param name="Duration">
/// The length of the active phase measured from the scenario's actual start.
/// Light measurements report an on state throughout this phase.
/// </param>
/// <param name="Target">
/// The configuration used to select devices capable of reporting light state.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether normal light behavior should be restored automatically.
/// Only automatic recovery is supported in the initial implementation.
/// </param>
/// <param name="RecoveryDuration">
/// Must be null because recovery restores normal light behavior immediately.
/// </param>
public sealed record class LightOnScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration)
: ScenarioDefinition(Name, StartsAfter, Duration, Target, AutoRecover, RecoveryDuration);
