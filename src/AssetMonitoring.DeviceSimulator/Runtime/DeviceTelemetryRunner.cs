using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Coordinates periodic generation and sending of telemetry
/// for individual simulated devices.
/// </summary>
public sealed class DeviceTelemetryRunner
{
    private readonly ILogger<DeviceTelemetryRunner> _logger;
    private readonly IDeviceSimulatorApiClient _apiClient;
    private readonly NormalTelemetryGenerator _telemetryGenerator;
    private readonly IOptions<DeviceSimulatorOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceTelemetryRunner"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used to report telemetry results and failures.
    /// </param>
    /// <param name="apiClient">
    /// The API client used to send generated measurements.
    /// </param>
    /// <param name="telemetryGenerator">
    /// The generator used to create measurements for device capabilities.
    /// </param>
    /// <param name="options">
    /// The options containing the telemetry interval and warehouse time zone.
    /// </param>
    /// <param name="timeProvider">
    /// The time provider used to schedule delays between telemetry cycles.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any constructor dependency is null.
    /// </exception>
    public DeviceTelemetryRunner(ILogger<DeviceTelemetryRunner> logger, IDeviceSimulatorApiClient apiClient, NormalTelemetryGenerator telemetryGenerator, IOptions<DeviceSimulatorOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(telemetryGenerator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _apiClient = apiClient;
        _telemetryGenerator = telemetryGenerator;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Generates and sends telemetry for one active device.
    /// The first cycle starts immediately. Each subsequent cycle starts
    /// after the configured delay following completion of the previous cycle.
    /// </summary>
    /// <param name="device">
    /// The active device whose capabilities determine the generated measurements.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop telemetry requests and interval delays.
    /// </param>
    /// <returns>
    /// A task representing the lifetime of the telemetry loop.
    /// Completes when the device has no capabilities.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty or its capabilities are null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device is not active or contains an unsupported capability.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when a telemetry request fails.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when a telemetry response is invalid.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when execution is canceled or a request times out.
    /// </exception>
    /// <exception cref="TimeZoneNotFoundException">
    /// Thrown when the configured warehouse time zone cannot be found.
    /// </exception>
    /// <exception cref="InvalidTimeZoneException">
    /// Thrown when the configured warehouse time zone data is invalid.
    /// </exception>
    public Task RunAsync(SimulatorDeviceResponse device, CancellationToken cancellationToken = default) 
        => RunAsync(device, null, _timeProvider.GetTimestamp(), cancellationToken);

    /// <summary>
    /// Generates and sends telemetry for one active device, optionally applying
    /// a high-temperature scenario to its temperature measurements.
    /// The first cycle starts immediately. Subsequent cycles start after
    /// the configured delay following completion of the previous cycle.
    /// </summary>
    /// <remarks>
    /// The scenario is applied once per new temperature measurement.
    /// HTTP retries reuse the resulting measurement without advancing
    /// the scenario again. Other metrics remain unchanged.
    /// </remarks>
    /// <param name="device">
    /// The active device whose capabilities determine the generated measurements.
    /// </param>
    /// <param name="scenarioSequence">
    /// The device's temperature scenario sequence, or null to generate normal telemetry.
    /// </param>
    /// <param name="simulationStartedAt">
    /// The simulation start timestamp obtained from the same time provider
    /// used by this runner. Share this timestamp across device loops
    /// to give their scenarios a common execution timeline.
    /// This value is a timestamp from GetTimestamp, not UTC date-time ticks.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop telemetry requests, retry delays, and interval delays.
    /// </param>
    /// <returns>
    /// A task representing the lifetime of the telemetry loop.
    /// Completes when the device has no telemetry capabilities.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty or its capabilities are null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when an applicable scenario receives a nonfinite temperature
    /// or a negative elapsed time.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device is not active, a capability is unsupported,
    /// an applicable temperature measurement has no numeric value,
    /// or the scenario reaches recovery with a missing or nonpositive duration.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when an applicable scenario has automatic recovery disabled.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when a nonretryable HTTP failure occurs or transient HTTP
    /// failures reach the configured failure limit.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when a telemetry response is invalid.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when execution is canceled, or request timeouts or independent
    /// request cancellations reach the configured failure limit.
    /// </exception>
    /// <exception cref="TimeZoneNotFoundException">
    /// Thrown when the configured warehouse time zone cannot be found.
    /// </exception>
    /// <exception cref="InvalidTimeZoneException">
    /// Thrown when the configured warehouse time zone data is invalid.
    /// </exception>
    public async Task RunAsync(SimulatorDeviceResponse device, DeviceTemperatureScenarioSequence? scenarioSequence, long simulationStartedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Id == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(device));
        }

        if (device.Lifecycle != SimulatorDeviceLifecycle.Active)
        {
            throw new InvalidOperationException("Only an active device can send telemetry.");
        }

        var options = _options.Value;
        var interval = options.TelemetryInterval;
        var warehouseTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.WarehouseTimeZoneId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var measurements = _telemetryGenerator.CreateMeasurements(device, warehouseTimeZone);

            if (measurements.Count == 0)
            {
                _logger.LogWarning("Device {DeviceCode} ({DeviceId}) has no telemetry capabilities. Its telemetry loop will stop.",
                    device.Code, device.Id);
                return;
            }

            foreach (var measurement in measurements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = _timeProvider.GetElapsedTime(simulationStartedAt);
                var outgoingMeasurement = ApplyTemperatureScenarios(measurement, scenarioSequence, elapsed);
                var result = await SendMeasurementWithRetryAsync(device, outgoingMeasurement, cancellationToken);

                _logger.LogInformation("Telemetry request completed for device {DeviceCode} ({DeviceId}). Metric: {Metric}, Measurement ID: {MeasurementId}, Recorded: {Recorded}.",
                    device.Code, device.Id, measurement.Metric, result.MeasurementId, result.Recorded);
            }

            await Task.Delay(interval, _timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Sends one measurement, retrying transient failures up to the configured limit.
    /// </summary>
    /// <remarks>
    /// Every attempt sends the same measurement, preserving its identifier,
    /// values, and timestamp. Caller cancellation and nonretryable failures
    /// propagate immediately.
    /// </remarks>
    /// <param name="device">
    /// The device that produced the measurement.
    /// </param>
    /// <param name="measurement">
    /// The existing measurement reused by every attempt.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel requests and retry delays.
    /// </param>
    /// <returns>
    /// The successful telemetry recording response.
    /// </returns>
    private async Task<SimulatorTelemetryRecordingResponse> SendMeasurementWithRetryAsync(SimulatorDeviceResponse device, SimulatorTelemetryMeasurementRequest measurement, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var maxFailures = options.MaxConsecutiveTelemetryFailures;
        var retryDelay = options.TelemetryInterval;
        var consecutiveFailures = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await _apiClient.SendTelemetryAsync(device.Id, measurement, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception
            is OperationCanceledException || (exception
            is HttpRequestException httpException && (httpException.StatusCode
            is null || (int)httpException.StatusCode.Value is >= 500 and <= 599))))
            {
                consecutiveFailures++;

                if (consecutiveFailures >= maxFailures)
                {
                    _logger.LogError(exception, "Telemetry runner stopped for device {DeviceCode} ({DeviceId}) after {ConsecutiveFailures} consecutive failures. Metric: {Metric}, Measurement ID: {MeasurementId}, Failure limit: {MaxFailures}.",
                        device.Code, device.Id, consecutiveFailures, measurement.Metric, measurement.MeasurementId, maxFailures);
                    throw;
                }
                _logger.LogWarning(exception, "Telemetry attempt failed for device {DeviceCode} ({DeviceId}). Metric: {Metric}, Measurement ID: {MeasurementId}. Consecutive failures: {ConsecutiveFailures}/{MaxFailures}. Next attempt in {RetryDelay}.",
                    device.Code, device.Id, measurement.Metric, measurement.MeasurementId, consecutiveFailures, maxFailures, retryDelay);
            }

            await Task.Delay(retryDelay, _timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Applies the device's temperature scenario sequence to a temperature measurement.
    /// Other metrics and measurements without a sequence are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Invoke once per new measurement, before sending it.
    /// HTTP retries must reuse the resulting measurement without advancing
    /// the sequence again.
    /// </remarks>
    /// <param name="measurement">
    /// The generated telemetry measurement to process.
    /// </param>
    /// <param name="scenarioSequence">
    /// The device's temperature scenario sequence, or null to leave
    /// the measurement unchanged.
    /// </param>
    /// <param name="elapsed">
    /// The elapsed time since simulation execution began.
    /// Supply nondecreasing values for successive calls to the same sequence.
    /// </param>
    /// <returns>
    /// A copy with the numeric value returned by the sequence when processing
    /// a temperature measurement with a nonnull sequence;
    /// otherwise, the original measurement.
    /// All other measurement properties are preserved.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a temperature measurement processed by the sequence
    /// has no numeric value, or the current scenario reaches recovery
    /// with a missing or nonpositive recovery duration.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a temperature measurement is processed by the sequence
    /// and its numeric value is not finite or the elapsed time is negative.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the sequence attempts to run a scenario
    /// with automatic recovery disabled.
    /// </exception>
    private static SimulatorTelemetryMeasurementRequest ApplyTemperatureScenarios(SimulatorTelemetryMeasurementRequest measurement, DeviceTemperatureScenarioSequence? scenarioSequence, TimeSpan elapsed)
    {
        if (scenarioSequence == null || measurement.Metric != SimulatorTelemetryMetric.Temperature)
        {
            return measurement;
        }

        var normalTemperature = measurement.NumericValue ?? throw new InvalidOperationException("A temperature measurement must contain a numeric value.");

        var nextTemperature = scenarioSequence.GetNextTemperature(normalTemperature, elapsed);
        return measurement with { NumericValue = nextTemperature };
    }
}
