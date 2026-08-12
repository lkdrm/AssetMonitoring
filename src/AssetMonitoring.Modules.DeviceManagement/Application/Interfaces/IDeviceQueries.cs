using AssetMonitoring.Modules.DeviceManagement.Application.Devices;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

/// <summary>
/// Provides read-only queries for retrieving device information.
/// </summary>
public interface IDeviceQueries
{
    /// <summary>
    /// Asynchronously retrieves devices, optionally filtered by lifecycle.
    /// </summary>
    /// <param name="lifecycle">
    /// The lifecycle used to filter devices, or <see langword="null"/>
    /// to retrieve devices in every lifecycle state.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// A read-only list containing the devices matching the requested filter.
    /// </returns>
    Task<IReadOnlyList<DeviceResponse>> GetAllAsync(DeviceLifecycle? lifecycle = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves a device by its unique catalog code.
    /// </summary>
    /// <param name="code">The unique catalog code of the device.</param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The matching device, or <see langword="null"/> when no device
    /// with the specified code exists.
    /// </returns>
    Task<DeviceResponse?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}
