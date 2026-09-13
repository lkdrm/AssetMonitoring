namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents the device catalog validation result returned by the API.
/// </summary>
/// <param name="IsValid">
/// Indicates whether the catalog passed validation.
/// </param>
/// <param name="Errors">
/// The validation errors. The collection is empty when the catalog is valid.
/// </param>
public sealed record DeviceCatalogValidationResponse(bool IsValid, IReadOnlyList<DeviceCatalogValidationErrorResponse> Errors);
