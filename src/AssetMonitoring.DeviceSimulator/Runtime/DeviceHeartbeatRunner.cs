using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Sends periodic heartbeat requests for individual simulated devices
/// using the configured interval and time provider.
/// </summary>
public sealed class DeviceHeartbeatRunner
{
    private readonly ILogger<DeviceHeartbeatRunner> _logger;
    private readonly IDeviceSimulatorApiClient _apiClient;
    private readonly IOptions<DeviceSimulatorOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceHeartbeatRunner"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used to report heartbeat results and failures.
    /// </param>
    /// <param name="apiClient">
    /// The API client used to send heartbeat requests.
    /// </param>
    /// <param name="options">
    /// The simulator options containing the heartbeat interval.
    /// </param>
    /// <param name="timeProvider">
    /// The time provider used to schedule delays between heartbeat requests.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any constructor dependency is null.
    /// </exception>
    public DeviceHeartbeatRunner(ILogger<DeviceHeartbeatRunner> logger, IDeviceSimulatorApiClient apiClient, IOptions<DeviceSimulatorOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _apiClient = apiClient;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Sends a heartbeat immediately and repeats after the configured delay
    /// following each completed request.
    /// </summary>
    /// <remarks>
    /// Retries transport failures, HTTP server errors, and request timeouts.
    /// A successful response resets the consecutive failure count.
    /// The original exception is rethrown when the configured limit is reached.
    /// Nonretryable failures and caller cancellation propagate immediately.
    /// </remarks>
    /// <param name="device">
    /// The active device whose heartbeats are sent.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to stop heartbeat requests and interval delays.
    /// </param>
    /// <returns>
    /// A task representing the lifetime of the device heartbeat loop.
    /// </returns>
    /// <exception cref="HttpRequestException">
    /// Thrown when a nonretryable HTTP failure occurs or a transient HTTP
    /// failure reaches the configured consecutive failure limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when execution is canceled, or when a request timeout
    /// or independent request cancellation reaches the configured
    /// consecutive failure limit.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device is not active.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when a heartbeat response is invalid.
    /// </exception>
    public async Task RunAsync(SimulatorDeviceResponse device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Id == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(device));
        }

        if (device.Lifecycle != SimulatorDeviceLifecycle.Active)
        {
            throw new InvalidOperationException("Only an active device can send heartbeats.");
        }

        var maxFailures = _options.Value.MaxConsecutiveHeartbeatFailures;
        var interval = _options.Value.HeartbeatInterval;
        var consecutiveFailures = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _apiClient.SendHeartbeatAsync(device.Id, cancellationToken);
                consecutiveFailures = 0;

                _logger.LogInformation("Heartbeat completed for device {DeviceCode} ({DeviceId}). Last heartbeat UTC: {LastHeartbeatAtUtc:O}, Changed: {Changed}.",
                    device.Code, result.DeviceId, result.LastHeartbeatAtUtc, result.Changed);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception
            is OperationCanceledException || (exception
            is HttpRequestException httpException && (httpException.StatusCode
            is null || (int)httpException.StatusCode.Value is >= 500 and <= 599))))
            {
                consecutiveFailures++;

                if (consecutiveFailures >= maxFailures)
                {
                    _logger.LogError(exception, "Heartbeat runner stopped for device {DeviceCode} ({DeviceId}) after {ConsecutiveFailures} consecutive failures. Failure limit: {MaxFailures}.",
                        device.Code, device.Id, consecutiveFailures, maxFailures);
                    throw;
                }
                _logger.LogWarning(exception, "Heartbeat attempt failed for device {DeviceCode} ({DeviceId}). Consecutive failures: {ConsecutiveFailures}/{MaxFailures}. Next attempt in {RetryDelay}.",
                    device.Code, device.Id, consecutiveFailures, maxFailures, interval);
            }

            await Task.Delay(interval, _timeProvider, cancellationToken);
        }
    }
}
