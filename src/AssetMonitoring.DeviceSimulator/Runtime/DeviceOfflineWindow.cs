using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Represents an immutable scheduled outage for one device.
/// </summary>
/// <remarks>
/// Contains no mutable execution state and can be queried concurrently
/// by heartbeat and telemetry loops.
/// Scenario configuration must be validated before construction.
/// </remarks>
public sealed class DeviceOfflineWindow
{
    private readonly Guid _deviceId;
    private readonly DeviceOfflineScenarioDefinition _definition;

    /// <summary>
    /// Gets the identifier of the device affected by the outage.
    /// </summary>
    public Guid DeviceId => _deviceId;

    /// <summary>
    /// Gets the definition that configures the outage window.
    /// </summary>
    public DeviceOfflineScenarioDefinition Definition => _definition;

    /// <summary>
    /// Initializes an outage window for the specified device.
    /// </summary>
    /// <param name="definition">
    /// The validated definition containing the outage start and duration.
    /// </param>
    /// <param name="deviceId">
    /// The nonempty identifier of the affected device.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="definition"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty.
    /// </exception>
    public DeviceOfflineWindow(DeviceOfflineScenarioDefinition definition, Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _definition = definition;
    }

    /// <summary>
    /// Determines whether the outage is active at the specified
    /// elapsed simulation time.
    /// </summary>
    /// <remarks>
    /// The start is inclusive and the end is exclusive.
    /// Queries do not change state and may be made in any order.
    /// </remarks>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since simulation startup.
    /// </param>
    /// <returns>
    /// True when the specified time falls within the outage window;
    /// otherwise, false.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elapsed"/> is negative.
    /// </exception>
    public bool IsActiveAt(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        return elapsed >= _definition.StartsAfter
            && elapsed < _definition.StartsAfter + _definition.Duration;
    }
}
