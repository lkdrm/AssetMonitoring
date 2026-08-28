using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Activation;

/// <summary>
/// Coordinates activation of device monitoring.
/// </summary>
public sealed class DeviceActivationService
{
    private readonly IDeviceRepository _deviceRepository;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceActivationService"/> class.
    /// </summary>
    /// <param name="deviceRepository">The repository used to load and persist devices.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="deviceRepository"/> is null.
    /// </exception>
    public DeviceActivationService(IDeviceRepository deviceRepository)
    {
        ArgumentNullException.ThrowIfNull(deviceRepository);

        _deviceRepository = deviceRepository;
    }

    /// <summary>
    /// Asynchronously activates monitoring for the specified device.
    /// Repeated activation is idempotent.
    /// </summary>
    /// <param name="deviceId">The identifier of the device to activate.</param>
    /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
    /// <returns>The activation result when the device exists; otherwise, null.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a retired device is activated before being restored.
    /// </exception>
    public async Task<DeviceActivationResult?> ActivateAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);

        if (device == null)
        {
            return null;
        }

        var activationResult = device.Activate();

        if (activationResult)
        {
            await _deviceRepository.SaveChangesAsync(cancellationToken);
        }

        return new DeviceActivationResult(device.Id, device.Lifecycle, activationResult);
    }
}