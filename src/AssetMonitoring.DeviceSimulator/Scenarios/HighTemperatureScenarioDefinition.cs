namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines a scenario that gradually raises the temperature of each targeted
/// device toward an abnormal target, holds it there, and then gradually restores
/// normal telemetry.
/// </summary>
/// <param name="Name">
/// The unique human-readable scenario name.
/// </param>
/// <param name="StartsAfter">
/// The delay between simulation startup and the beginning of the temperature
/// increase.
/// </param>
/// <param name="Duration">
/// The length of the active phase measured from the scenario's actual start.
/// The active phase includes the gradual temperature rise, so the time spent at
/// the abnormal target is shorter than this value.
/// </param>
/// <param name="Target">
/// The configuration used to select a temperature-capable device.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether the temperature should return automatically to its normal
/// range.
/// </param>
/// <param name="RecoveryDuration">
/// The planned length of the gradual return to normal temperature.
/// Recovery can take longer when <paramref name="MaximumRecoveryPerMeasurement"/>
/// limits the decrease per measurement.
/// </param>
/// <param name="AbnormalMinimum">
/// The inclusive lower bound used to choose the abnormal target temperature, in °C.
/// Values generated during the rise can be lower than this bound.
/// </param>
/// <param name="AbnormalMaximum">
/// The exclusive upper bound used to choose the abnormal target temperature, in °C.
/// Must be greater than <paramref name="AbnormalMinimum"/>.
/// </param>
/// <param name="MaximumRisePerMeasurement">
/// The maximum increase between two consecutive measurements, in °C per measurement
/// (not per second).
/// </param>
/// <param name="MaximumRecoveryPerMeasurement">
/// The maximum decrease between two consecutive recovery measurements,
/// in °C per measurement. This limit takes priority over the recovery deadline.
/// </param>

public sealed record HighTemperatureScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration, double AbnormalMinimum, double AbnormalMaximum, double MaximumRisePerMeasurement, double MaximumRecoveryPerMeasurement)
    : ScenarioDefinition(Name, StartsAfter, Duration, Target, AutoRecover, RecoveryDuration);
