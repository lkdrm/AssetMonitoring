using AssetMonitoring.DeviceSimulator.Api.Contracts;
using Microsoft.Extensions.Logging;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Coordinates concurrent telemetry execution for prepared devices.
/// Isolates device failures and supports cancellation of all telemetry loops.
/// </summary>
public sealed class DeviceTelemetryCoordinator
{
    private readonly ILogger<DeviceTelemetryCoordinator> _logger;
    private readonly DeviceTelemetryRunner _telemetryRunner;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceTelemetryCoordinator"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used to report device telemetry loop termination.
    /// </param>
    /// <param name="telemetryRunner">
    /// The runner used to generate and send telemetry for each device.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any constructor dependency is null.
    /// </exception>
    public DeviceTelemetryCoordinator(ILogger<DeviceTelemetryCoordinator> logger, DeviceTelemetryRunner telemetryRunner)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(telemetryRunner);

        _logger = logger;
        _telemetryRunner = telemetryRunner;
    }

    /// <summary>
    /// Runs telemetry loops concurrently and waits until every loop has ended.
    /// </summary>
    /// <param name="devices">
    /// The prepared active devices whose telemetry loops are started.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop all device telemetry loops.
    /// </param>
    /// <returns>
    /// A task that completes when all device telemetry loops have ended.
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
    /// Runs telemetry for one device, handling caller cancellation
    /// and logging terminal failures without affecting other device loops.
    /// </summary>
    /// <param name="device">
    /// The prepared device whose telemetry loop is started.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop the device telemetry loop.
    /// </param>
    /// <returns>
    /// A task that completes when the loop ends, is canceled,
    /// or encounters a terminal failure.
    /// </returns>
    private async Task RunDeviceAsync(SimulatorDeviceResponse device, CancellationToken cancellationToken = default)
    {
        try
        {
            await _telemetryRunner.RunAsync(device, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Telemetry loop stopped for device {DeviceCode} ({DeviceId}) because cancellation was requested.",
                device.Code, device.Id);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Telemetry loop failed for device {DeviceCode} ({DeviceId}). This device will no longer send telemetry.",
                device.Code, device.Id);
        }
    }
}
