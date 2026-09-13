namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents a device catalog validation error returned by the API.
/// </summary>
/// <param name="ErrorCode">
/// The machine-readable code identifying the validation rule.
/// </param>
/// <param name="Message">
/// The human-readable description of the validation error.
/// </param>
/// <param name="DeviceCode">
/// The affected device code, or null for a catalog-level error.
/// </param>
/// <param name="PropertyName">
/// The affected property name, or null when no specific property applies.
/// </param>
public sealed record DeviceCatalogValidationErrorResponse(string ErrorCode, string Message, string? DeviceCode, string? PropertyName);
