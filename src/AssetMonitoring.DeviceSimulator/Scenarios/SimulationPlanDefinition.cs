namespace AssetMonitoring.DeviceSimulator.Scenarios;

/// <summary>
/// Represents a complete, selectable device simulation plan.
/// </summary>
/// <param name="Name">
/// The unique human-readable name of the simulation plan.
/// </param>
/// <param name="Seed">
/// The optional seed used for deterministic device selection and telemetry
/// generation. When null, a seed is generated when the simulation starts.
/// </param>
/// <param name="Scenarios">
/// The abnormal scenarios executed as part of the plan. An empty collection
/// represents normal warehouse operation.
/// </param>
public sealed record SimulationPlanDefinition(string Name, int? Seed, IReadOnlyList<ScenarioDefinition> Scenarios);
