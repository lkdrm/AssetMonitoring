using AssetMonitoring.DeviceSimulator.Api.Contracts;
using Microsoft.Extensions.Logging;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Coordinates concurrent heartbeat execution for prepared devices.
/// Keeps device failures isolated and supports cancellation
/// of all device heartbeat loops.
/// </summary>
public sealed class DeviceHeartbeatCoordinator
{
    private readonly ILogger<DeviceHeartbeatCoordinator> _logger;
    private readonly DeviceHeartbeatRunner _heartbeatRunner;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceHeartbeatCoordinator"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used to report device heartbeat loop termination.
    /// </param>
    /// <param name="heartbeatRunner">
    /// The runner used to send heartbeats for each device.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any constructor dependency is null.
    /// </exception>
    public DeviceHeartbeatCoordinator(ILogger<DeviceHeartbeatCoordinator> logger, DeviceHeartbeatRunner heartbeatRunner)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(heartbeatRunner);

        _logger = logger;
        _heartbeatRunner = heartbeatRunner;
    }

    /// <summary>
    /// Runs heartbeat loops concurrently for the supplied devices
    /// and waits until every loop has finished.
    /// </summary>
    /// <param name="devices">
    /// The prepared active devices whose heartbeat loops are started.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop all device heartbeat loops.
    /// </param>
    /// <returns>
    /// A task that completes when all device heartbeat loops have ended.
    /// An empty collection completes immediately.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/> is null.
    /// </exception>
    public async Task RunAsync(IReadOnlyList<SimulatorDeviceResponse> devices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var tasks = devices.Select(device => RunDeviceAsync(device, cancellationToken)).ToArray();

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Runs the heartbeat loop for one device.
    /// Handles caller cancellation and logs terminal failures
    /// without propagating them to other device loops.
    /// </summary>
    /// <param name="device">
    /// The prepared device whose heartbeat loop is started.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop the device heartbeat loop.
    /// </param>
    /// <returns>
    /// A task that completes when the device loop ends,
    /// is canceled, or encounters a terminal failure.
    /// </returns>
    private async Task RunDeviceAsync(SimulatorDeviceResponse device, CancellationToken cancellationToken = default)
    {
        try
        {
            await _heartbeatRunner.RunAsync(device, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Heartbeat loop stopped for device {DeviceCode} ({DeviceId}) because cancellation was requested.",
                device.Code, device.Id);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Heartbeat loop failed for device {DeviceCode} ({DeviceId}). This device will no longer send heartbeats.",
                device.Code, device.Id);
        }
    }
}
