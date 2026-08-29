using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Heartbeat;

/// <summary>
/// Coordinates recording heartbeat timestamps for active devices.
/// </summary>
public sealed class DeviceHeartbeatService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceHeartbeatService"/> class.
    /// </summary>
    /// <param name="deviceRepository">
    /// The repository used to load and persist devices.
    /// </param>
    /// <param name="timeProvider">
    /// The provider used to obtain the current server time.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="deviceRepository"/> or
    /// <paramref name="timeProvider"/> is null.
    /// </exception>
    public DeviceHeartbeatService(IDeviceRepository deviceRepository, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(deviceRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _deviceRepository = deviceRepository;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Asynchronously records a server-generated heartbeat timestamp for the specified device.
    /// </summary>
    /// <param name="deviceId">
    /// The identifier of the device reporting the heartbeat.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// The heartbeat result when the device exists; otherwise,
    /// <see langword="null"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device is not active.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is canceled.
    /// </exception>
    public async Task<DeviceHeartbeatResult?> RecordAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, cancellationToken);

        if (device is null)
        {
            return null;
        }

        var heartbeatAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var recorded = device.RecordHeartbeat(heartbeatAtUtc);

        if (recorded)
        {
            await _deviceRepository.SaveChangesAsync(cancellationToken);
        }

        return new DeviceHeartbeatResult(device.Id, device.LastHeartbeatAtUtc.Value, recorded);
    }
}
