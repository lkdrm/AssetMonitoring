namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

/// <summary>
/// Represents a single validation error found in the device catalog.
/// </summary>
/// <param name="ErrorCode">Machine-readable code identifying the validation rule.</param>
/// <param name="Message">Human-readable description of the validation error.</param>
/// <param name="DeviceCode">Code of the affected device, or null for a catalog-level error.</param>
/// <param name="PropertyName">Name of the affected property, or null when the error is not related to a specific property.</param>
public sealed record class DeviceCatalogValidationError(string ErrorCode, string Message, string? DeviceCode = null, string? PropertyName = null);
