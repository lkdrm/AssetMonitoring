using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Executes scenario runtimes sequentially for one device
/// and one telemetry metric.
/// </summary>
/// <remarks>
/// Scenarios are ordered by their configured start delays.
/// Each scenario waits until its start delay has elapsed and the previous
/// scenario has completed, including recovery.
/// Calls must be sequential and use nondecreasing elapsed simulation time.
/// The caller is responsible for supplying measurements from this device.
/// </remarks>
public sealed class DeviceMetricScenarioSequence
{
    private readonly IReadOnlyList<IMeasurementScenarioRuntime> _scenarios;
    private int _currentScenarioIndex;
    private TimeSpan? _currentScenarioStartedAt;

    /// <summary>
    /// Gets the identifier of the device whose measurements
    /// are processed by this sequence.
    /// </summary>
    public Guid DeviceId { get; }

    /// <summary>
    /// Gets the telemetry metric processed by this sequence.
    /// </summary>
    public SimulatorTelemetryMetric Metric { get; }

    /// <summary>
    /// Gets the scenario currently selected for processing,
    /// or null when the sequence is empty or exhausted.
    /// </summary>
    /// <remarks>
    /// The selected scenario may still be waiting for its start delay.
    /// A non-null value does not necessarily indicate an active scenario.
    /// </remarks>
    public IMeasurementScenarioRuntime? CurrentScenario => _currentScenarioIndex < _scenarios.Count ? _scenarios[_currentScenarioIndex] : null;

    /// <summary>
    /// Initializes a sequence for the specified device and metric.
    /// </summary>
    /// <remarks>
    /// Stores a copy of the supplied collection ordered by scenario start delay.
    /// Scenarios with equal start delays retain their original input order.
    /// Runtime instances themselves are reused rather than copied.
    /// </remarks>
    /// <param name="deviceId">
    /// The nonempty identifier shared by every scenario in the sequence.
    /// </param>
    /// <param name="metric">
    /// The telemetry metric affected by every scenario in the sequence.
    /// </param>
    /// <param name="scenarios">
    /// The scenario runtimes to execute.
    /// An empty collection is allowed.
    /// Every item must be non-null and belong to the specified device and metric.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="scenarios"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty,
    /// or when the collection contains a null item or a scenario
    /// associated with a different device or metric.
    /// </exception>
    public DeviceMetricScenarioSequence(Guid deviceId, SimulatorTelemetryMetric metric, IReadOnlyList<IMeasurementScenarioRuntime> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        foreach (var scenario in scenarios)
        {
            if (scenario is null)
            {
                throw new ArgumentException("The scenario collection cannot contain null items.", nameof(scenarios));
            }

            if (scenario.DeviceId != deviceId)
            {
                throw new ArgumentException("Every scenario must belong to the specified device.", nameof(scenarios));
            }

            if (scenario.Metric != metric)
            {
                throw new ArgumentException("Every scenario must change the specified metric.", nameof(scenarios));
            }
        }

        DeviceId = deviceId;
        Metric = metric;

        _scenarios = scenarios.OrderBy(scenario => scenario.Definition.StartsAfter).ToList();
    }

    /// <summary>
    /// Applies the current eligible scenario to a measurement
    /// and advances the sequence when that scenario completes.
    /// </summary>
    /// <remarks>
    /// The original measurement is returned while waiting for a scenario
    /// or when no scenarios remain.
    /// The actual start is captured on the first eligible measurement,
    /// preserving the scenario's full active duration after any queue delay.
    /// Each call applies at most one scenario.
    /// When a scenario completes, the next scenario can start only
    /// on a subsequent call.
    /// Invoke this method once per new measurement; delivery retries
    /// must reuse the previously returned measurement.
    /// </remarks>
    /// <param name="measurement">
    /// The non-null measurement containing the normal value to process.
    /// Its metric must match <see cref="Metric"/>.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since the shared simulation start.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The measurement returned by the current scenario,
    /// or the original measurement when no scenario is eligible.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="measurement"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the measurement's metric does not match
    /// the metric handled by this sequence.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elapsed"/> is negative.
    /// </exception>
    public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Metric != Metric)
        {
            throw new ArgumentException($"The sequence applies only to {Metric} measurements.", nameof(measurement));
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        var scenario = CurrentScenario;

        if (scenario is null || elapsed < scenario.Definition.StartsAfter)
        {
            return measurement;
        }

        _currentScenarioStartedAt ??= elapsed;

        var scenarioElapsed = scenario.Definition.StartsAfter + (elapsed - _currentScenarioStartedAt.Value);

        var result = scenario.Apply(measurement, scenarioElapsed);

        if (scenario.Phase == ScenarioPhase.Completed)
        {
            _currentScenarioIndex++;
            _currentScenarioStartedAt = null;
        }

        return result;
    }
}