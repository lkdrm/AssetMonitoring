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
    /// passing each device its optional temperature scenario sequence
    /// and a shared simulation start timestamp.
    /// </summary>
    /// <remarks>
    /// A device without an entry in the sequence dictionary runs with
    /// normal telemetry. An empty dictionary enables normal telemetry
    /// for all devices.
    /// Terminal failures and caller-requested cancellation are handled
    /// separately for each device. A failure in one device loop does not
    /// stop the remaining loops.
    /// </remarks>
    /// <param name="devices">
    /// The prepared active devices whose telemetry loops are started.
    /// </param>
    /// <param name="sequencesByDevice">
    /// The prepared temperature scenario sequences keyed by device identifier.
    /// Devices without temperature scenarios may be absent from the dictionary.
    /// </param>
    /// <param name="simulationStartedAt">
    /// The shared timestamp captured before starting the telemetry loops
    /// using the same TimeProvider as the telemetry runner.
    /// This is a value obtained from GetTimestamp(), not UTC date-time ticks
    /// or an elapsed duration.
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
    /// <paramref name="sequencesByDevice"/> is null.
    /// </exception>
    public async Task RunAsync(IReadOnlyList<SimulatorDeviceResponse> devices, IReadOnlyDictionary<Guid, DeviceTemperatureScenarioSequence> sequencesByDevice, long simulationStartedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sequencesByDevice);

        var tasks = new List<Task>();

        foreach (var device in devices)
        {
            sequencesByDevice.TryGetValue(device.Id, out var sequence);
            tasks.Add(RunDeviceAsync(device, sequence, simulationStartedAt, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Runs telemetry for one device with an optional temperature scenario
    /// sequence, handling cancellation and isolating terminal failures.
    /// </summary>
    /// <remarks>
    /// Caller-requested cancellation is logged as a normal stop.
    /// Other exceptions are logged as terminal device failures
    /// without stopping the remaining device loops.
    /// </remarks>
    /// <param name="device">
    /// The prepared device whose telemetry loop is started.
    /// </param>
    /// <param name="scenarioSequence">
    /// The temperature scenario sequence prepared for this device,
    /// or null to generate normal telemetry without temperature scenarios.
    /// </param>
    /// <param name="simulationStartedAt">
    /// The shared simulation start timestamp, forwarded unchanged to the runner.
    /// It must originate from the same TimeProvider used by the runner
    /// to calculate elapsed simulation time.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop the device telemetry loop.
    /// </param>
    /// <returns>
    /// A task that completes when the device telemetry loop ends,
    /// is canceled by the caller, or encounters a handled terminal failure.
    /// </returns>
    private async Task RunDeviceAsync(SimulatorDeviceResponse device, DeviceTemperatureScenarioSequence? scenarioSequence, long simulationStartedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            await _telemetryRunner.RunAsync(device, scenarioSequence, simulationStartedAt, cancellationToken);
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
