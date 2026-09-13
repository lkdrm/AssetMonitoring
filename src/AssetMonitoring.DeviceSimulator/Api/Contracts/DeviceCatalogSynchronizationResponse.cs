namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents the result of device catalog synchronization returned by the API.
/// </summary>
/// <param name="ValidationResult">
/// The catalog validation result, including any discovered errors.
/// </param>
/// <param name="Created">The number of newly created devices.</param>
/// <param name="Updated">The number of devices whose metadata was updated.</param>
/// <param name="Unchanged">The number of devices that required no changes.</param>
/// <param name="Retired">The number of devices retired during synchronization.</param>
/// <param name="Restored">The number of previously retired devices restored.</param>
/// <param name="Applied">
/// Indicates whether validation passed and synchronization was applied.
/// </param>
/// <param name="HasChanges">
/// Indicates whether synchronization created, updated, retired,
/// or restored any devices.
/// </param>
public sealed record DeviceCatalogSynchronizationResponse(DeviceCatalogValidationResponse ValidationResult, int Created, int Updated, int Unchanged, int Retired, int Restored, bool Applied, bool HasChanges);
