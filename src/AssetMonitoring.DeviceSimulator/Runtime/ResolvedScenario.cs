using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Represents a simulation scenario together with the devices
/// selected for its execution.
/// </summary>
/// <param name="Definition">
/// The validated scenario definition.
/// </param>
/// <param name="Devices">
/// The compatible active devices selected for the scenario.
/// </param>
public sealed record ResolvedScenario(ScenarioDefinition Definition, IReadOnlyList<SimulatorDeviceResponse> Devices);
