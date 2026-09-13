namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Defines a scenario that gradually increases one device's temperature,
/// maintains an abnormal range, and optionally restores normal behavior.
/// </summary>
/// <param name="Name">
/// The unique human-readable scenario name.
/// </param>
/// <param name="StartsAfter">
/// The delay between simulation startup and the beginning of the temperature
/// increase.
/// </param>
/// <param name="Duration">
/// The amount of time for which the generated temperature remains within the
/// abnormal range.
/// </param>
/// <param name="Target">
/// The configuration used to select a temperature-capable device.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether the temperature should return automatically to its normal
/// range.
/// </param>
/// <param name="RecoveryDuration">
/// The optional amount of time allocated for gradual temperature recovery.
/// </param>
/// <param name="AbnormalMinimum">
/// The inclusive minimum temperature generated during the abnormal phase.
/// </param>
/// <param name="AbnormalMaximum">
/// The inclusive maximum temperature generated during the abnormal phase.
/// </param>
/// <param name="MaximumRisePerMeasurement">
/// The maximum temperature increase allowed between consecutive measurements.
/// </param>
/// <param name="MaximumRecoveryPerMeasurement">
/// The maximum temperature decrease allowed between consecutive recovery
/// measurements.
/// </param>
public sealed record HighTemperatureScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration, double AbnormalMinimum, double AbnormalMaximum, double MaximumRisePerMeasurement, double MaximumRecoveryPerMeasurement)
    : ScenarioDefinition(Name, StartsAfter, Duration, Target, AutoRecover, RecoveryDuration);
