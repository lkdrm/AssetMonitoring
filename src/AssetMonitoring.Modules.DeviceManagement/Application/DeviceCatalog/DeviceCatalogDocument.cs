namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

/// <summary>
/// Represents the root document of the device catalog JSON file.
/// </summary>
/// <remarks>
/// The document contains raw catalog data that must be validated before
/// devices are created, updated, restored, or retired.
/// </remarks>
public sealed class DeviceCatalogDocument
{
    /// <summary>
    /// Gets the device definitions contained in the catalog.
    /// </summary>
    public required List<DeviceCatalogItem> Devices { get; init; }
}
