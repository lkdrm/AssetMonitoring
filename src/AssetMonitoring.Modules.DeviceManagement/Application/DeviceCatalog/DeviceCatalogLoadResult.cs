using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

/// <summary>
/// Represents the result of loading and validating a device catalog.
/// </summary>
/// <param name="Document">The device catalog deserialized from the source.</param>
/// <param name="ValidationResult">
/// The result of validating the loaded device catalog.
/// </param>
public sealed record DeviceCatalogLoadResult(DeviceCatalogDocument Document, DeviceCatalogValidationResult ValidationResult)
{
    /// <summary>
    /// Gets a value indicating whether the loaded catalog is valid.
    /// </summary>
    public bool IsValid => ValidationResult.IsValid;
}
