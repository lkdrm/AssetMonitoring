namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

/// <summary>
/// Represents the complete result of device catalog validation.
/// </summary>
public sealed class DeviceCatalogValidationResult
{
    /// <summary>
    /// Gets a value indicating whether the device catalog passed all validation rules.
    /// </summary>
    public bool IsValid => _errors.Count == 0;

    private readonly List<DeviceCatalogValidationError> _errors;

    /// <summary>
    /// Gets all validation errors found in the device catalog.
    /// The collection is empty when the catalog is valid.
    /// </summary>
    public IReadOnlyList<DeviceCatalogValidationError> Errors => _errors;

    /// <summary>
    /// Creates a validation result from the provided errors.
    /// </summary>
    /// <param name="deviceCatalogValidationErrors">Validation errors collected while checking the catalog.</param>
    /// <exception cref="ArgumentNullException">Thrown when the error collection is null.</exception>
    public DeviceCatalogValidationResult(IEnumerable<DeviceCatalogValidationError> deviceCatalogValidationErrors)
    {
        ArgumentNullException.ThrowIfNull(deviceCatalogValidationErrors);
        _errors = deviceCatalogValidationErrors.ToList();
    }
}
