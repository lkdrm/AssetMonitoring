using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

/// <summary>
/// Defines a contract for asynchronously reading a device catalog.
/// </summary>
public interface IDeviceCatalogReader
{
    /// <summary>
    /// Reads a device catalog from the specified file.
    /// </summary>
    /// <param name="path">Path to the device catalog file.</param>
    /// <param name="cancellationToken">Token used to cancel the asynchronous operation.</param>
    /// <returns>A task whose result contains the loaded device catalog document.</returns>
    Task<DeviceCatalogDocument> ReadAsync(string path, CancellationToken cancellationToken = default);
}
