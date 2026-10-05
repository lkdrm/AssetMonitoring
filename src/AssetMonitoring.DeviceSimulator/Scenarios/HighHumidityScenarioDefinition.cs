namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines a scenario that gradually raises the humidity of each targeted
/// device toward an abnormal target and restores normal humidity through
/// automatic recovery.
/// </summary>
/// <param name="Name">
/// The unique human-readable scenario name.
/// </param>
/// <param name="StartsAfter">
/// The earliest elapsed simulation time at which the scenario may begin.
/// A preceding scenario for the same device and metric can delay the actual start.
/// </param>
/// <param name="Duration">
/// The length of the active phase measured from the scenario's actual start.
/// This phase includes the gradual humidity rise; the target is held only for
/// the remaining active time after it has been reached.
/// </param>
/// <param name="Target">
/// The configuration used to select humidity-capable devices.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether humidity should return automatically to normal behavior.
/// </param>
/// <param name="RecoveryDuration">
/// The planned length of the gradual return to a captured normal humidity value.
/// Recovery can take longer when <paramref name="MaximumRecoveryPerMeasurement"/>
/// limits the decrease per measurement.
/// </param>
/// <param name="AbnormalMinimum">
/// The inclusive lower bound used to choose the abnormal humidity target,
/// expressed as a percentage between 0 and 100.
/// Values generated during the rise can be lower than this bound.
/// </param>
/// <param name="AbnormalMaximum">
/// The exclusive upper bound used to choose the abnormal humidity target,
/// expressed as a percentage no greater than 100.
/// Must be greater than <paramref name="AbnormalMinimum"/>.
/// </param>
/// <param name="MaximumRisePerMeasurement">
/// The maximum increase between consecutive active measurements, expressed in
/// percentage points per measurement rather than per second.
/// </param>
/// <param name="MaximumRecoveryPerMeasurement">
/// The maximum decrease between consecutive recovery measurements, expressed
/// in percentage points per measurement. This limit takes priority over the
/// planned recovery deadline.
/// </param>
public sealed record class HighHumidityScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration, double AbnormalMinimum, double AbnormalMaximum, double MaximumRisePerMeasurement, double MaximumRecoveryPerMeasurement)
    : ScenarioDefinition(Name, StartsAfter, Duration, Target, AutoRecover, RecoveryDuration);
