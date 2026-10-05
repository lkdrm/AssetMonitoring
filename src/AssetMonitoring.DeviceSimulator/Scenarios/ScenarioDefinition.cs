using System.Text.Json.Serialization;

namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Represents the common configuration shared by every abnormal simulation
/// scenario.
/// </summary>
/// <param name="Name">
/// The unique human-readable scenario name.
/// </param>
/// <param name="StartsAfter">
/// The delay between simulation startup and scenario activation.
/// </param>
/// <param name="Duration">
/// The amount of time for which the abnormal scenario remains active before
/// recovery begins.
/// </param>
/// <param name="Target">
/// The configuration used to select devices affected by the scenario.
/// </param>
/// <param name="AutoRecover">
/// Indicates whether normal device behavior should be restored automatically.
/// </param>
/// <param name="RecoveryDuration">
/// The optional amount of time required to return gradually to normal
/// operation.
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HighTemperatureScenarioDefinition), "HighTemperature")]
[JsonDerivedType(typeof(HighHumidityScenarioDefinition), "HighHumidity")]
public abstract record ScenarioDefinition(string Name, TimeSpan StartsAfter, TimeSpan Duration, ScenarioTargetDefinition Target, bool AutoRecover, TimeSpan? RecoveryDuration);
