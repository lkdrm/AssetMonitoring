using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;

/// <summary>
/// Represents the result of synchronizing a device catalog.
/// </summary>
/// <param name="ValidationResult">The catalog validation result.</param>
/// <param name="Created">The number of created devices.</param>
/// <param name="Updated">The number of updated devices.</param>
/// <param name="Unchanged">The number of unchanged devices.</param>
/// <param name="Retired">The number of retired devices.</param>
/// <param name="Restored">The number of restored devices.</param>
public sealed record class DeviceCatalogSynchronizationResult(DeviceCatalogValidationResult ValidationResult, int Created, int Updated, int Unchanged, int Retired, int Restored)
{
    /// <summary>
    /// Gets a value indicating whether the catalog passed validation
    /// and the synchronization was successfully applied.
    /// </summary>
    public bool Applied => ValidationResult.IsValid;

    /// <summary>
    /// Gets a value indicating whether synchronization changed any devices.
    /// </summary>
    public bool HasChanges =>
        Created > 0 ||
        Updated > 0 ||
        Retired > 0 ||
        Restored > 0;
}
