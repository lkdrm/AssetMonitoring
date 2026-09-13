namespace AssetMonitoring.DeviceSimulator.Configuration.Validation;

/// <summary>
/// Represents the complete result of simulation plan validation.
/// </summary>
/// <param name="Errors">
/// All validation errors discovered in the simulation plan.
/// </param>
public sealed record SimulationPlanValidationResult(IReadOnlyList<SimulationPlanValidationError> Errors)
{
    /// <summary>
    /// Gets a value indicating whether the simulation plan passed all
    /// validation rules.
    /// </summary>
    public bool IsValid => Errors.Count == 0;
}
