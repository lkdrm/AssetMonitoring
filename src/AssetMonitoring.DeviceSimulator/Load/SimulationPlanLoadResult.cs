using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Load;

/// <summary>
/// Represents a loaded simulation plan and its validation result.
/// </summary>
/// <param name="Plan">
/// The simulation plan read from the selected configuration file.
/// </param>
/// <param name="Validation">
/// The validation result containing any discovered configuration errors.
/// </param>
public sealed record SimulationPlanLoadResult(SimulationPlanDefinition Plan, SimulationPlanValidationResult Validation)
{
    /// <summary>
    /// Gets a value indicating whether the loaded plan passed validation.
    /// </summary>
    public bool IsValid => Validation.IsValid;
}
