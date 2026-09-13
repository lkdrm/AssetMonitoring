namespace AssetMonitoring.DeviceSimulator.Configuration.Validation;

/// <summary>
/// Represents a single simulation plan validation error.
/// </summary>
/// <param name="Code">
/// The stable machine-readable validation error code.
/// </param>
/// <param name="Path">
/// The path to the invalid simulation plan property.
/// </param>
/// <param name="Message">
/// The human-readable description of the validation failure.
/// </param>
public sealed record SimulationPlanValidationError(string Code, string Path, string Message);
