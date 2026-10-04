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
    /// Runs telemetry loops concurrently for prepared devices,
    /// passing each device its optional scenario schedule
    /// and a shared simulation start timestamp.
    /// </summary>
    /// <remarks>
    /// Devices without a schedule generate normal telemetry.
    /// An empty dictionary enables normal telemetry for all devices.
    /// Terminal failures and caller-requested cancellation are handled
    /// separately for each device without stopping the remaining loops.
    /// </remarks>
    /// <param name="devices">
    /// The prepared active devices whose telemetry loops are started.
    /// </param>
    /// <param name="schedule">
    /// The prepared scenario schedules keyed by device identifier.
    /// Devices without scenarios may be absent from the dictionary.
    /// </param>
    /// <param name="simulationStartedAt">
    /// The shared timestamp obtained from GetTimestamp() before starting
    /// the loops, using the same TimeProvider as the telemetry runner.
    /// This value is not a UTC timestamp or an elapsed duration.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop all device telemetry loops.
    /// </param>
    /// <returns>
    /// A task that completes when all device telemetry loops have ended.
    /// An empty device collection completes immediately.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="devices"/> or
    /// <paramref name="schedule"/> is null.
    /// </exception>
    public async Task RunAsync(IReadOnlyList<SimulatorDeviceResponse> devices, IReadOnlyDictionary<Guid, DeviceScenarioSchedule> schedule, long simulationStartedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(schedule);

        var tasks = new List<Task>();

        foreach (var device in devices)
        {
            schedule.TryGetValue(device.Id, out var deviceSchedule);
            tasks.Add(RunDeviceAsync(device, deviceSchedule, simulationStartedAt, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Runs telemetry for one device with an optional scenario schedule,
    /// handling cancellation and isolating terminal failures.
    /// </summary>
    /// <remarks>
    /// Caller-requested cancellation is logged as a normal stop.
    /// Other exceptions are logged as terminal device failures
    /// without stopping the remaining device loops.
    /// </remarks>
    /// <param name="device">
    /// The prepared device whose telemetry loop is started.
    /// </param>
    /// <param name="schedule">
    /// The scenario schedule prepared for this device,
    /// or null to generate normal telemetry without scenarios.
    /// </param>
    /// <param name="simulationStartedAt">
    /// The shared simulation start timestamp, forwarded unchanged.
    /// It must originate from the same TimeProvider used by the runner
    /// to calculate elapsed simulation time.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop the device telemetry loop.
    /// </param>
    /// <returns>
    /// A task that completes when the loop ends, is canceled by the caller,
    /// or encounters a handled terminal failure.
    /// </returns>
    private async Task RunDeviceAsync(SimulatorDeviceResponse device, DeviceScenarioSchedule? schedule, long simulationStartedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            await _telemetryRunner.RunAsync(device, schedule, simulationStartedAt, cancellationToken);
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
