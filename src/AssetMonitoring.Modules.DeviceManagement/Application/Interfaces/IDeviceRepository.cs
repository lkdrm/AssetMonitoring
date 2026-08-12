using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

/// <summary>
/// Defines persistence operations for device entities.
/// </summary>
public interface IDeviceRepository
{
    /// <summary>
    /// Asynchronously gets all registered devices.
    /// </summary>
    /// <param name="cancellationToken"> A token used to cancel the asynchronous operation.</param>
    /// <returns>A read-only list containing all persisted devices.</returns>
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new device to the persistence context.
    /// </summary>
    /// <param name="device">The device to add.</param>
    void Add(Device device);

    /// <summary>
    /// Asynchronously persists all pending device changes.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
