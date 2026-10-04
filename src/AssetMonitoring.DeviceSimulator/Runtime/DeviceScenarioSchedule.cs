using AssetMonitoring.DeviceSimulator.Api.Contracts;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Routes a device's telemetry measurements to the scenario sequence
/// associated with each measurement's metric.
/// </summary>
/// <remarks>
/// Each metric has its own sequence, so scenarios for different metrics
/// can progress independently on the same device.
/// Sequences are stateful and must be called sequentially with nondecreasing
/// elapsed simulation time.
/// The caller is responsible for supplying measurements from this device.
/// </remarks>
public sealed class DeviceScenarioSchedule
{
    private readonly Dictionary<SimulatorTelemetryMetric, DeviceMetricScenarioSequence> _sequenceByMetric;
    
    /// <summary>
    /// Gets the identifier of the device whose measurements
    /// are processed by this schedule.
    /// </summary>
    public Guid DeviceId { get; }

    /// <summary>
    /// Initializes a schedule containing at most one sequence per metric
    /// for the specified device.
    /// </summary>
    /// <remarks>
    /// Copies the supplied collection into a private dictionary.
    /// The sequence instances themselves are reused rather than copied.
    /// Creating a schedule does not advance its sequences.
    /// </remarks>
    /// <param name="deviceId">
    /// The nonempty identifier shared by every sequence in the schedule.
    /// </param>
    /// <param name="sequences">
    /// The sequences to associate with their telemetry metrics.
    /// An empty collection is allowed.
    /// Every item must be non-null and belong to the specified device.
    /// No two items may handle the same metric.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="sequences"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="deviceId"/> is empty,
    /// the collection contains a null item or a sequence for another device,
    /// or multiple sequences handle the same metric.
    /// </exception>
    public DeviceScenarioSchedule(Guid deviceId, IEnumerable<DeviceMetricScenarioSequence> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        var sequenceByMetric = new Dictionary<SimulatorTelemetryMetric, DeviceMetricScenarioSequence>();

        foreach (var sequence in sequences)
        {
            if (sequence is null || sequence.DeviceId != deviceId)
            {
                throw new ArgumentException("Every sequence must belong to the specified device.", nameof(sequences));
            }

            if (!sequenceByMetric.TryAdd(sequence.Metric, sequence))
            {
                throw new ArgumentException($"Only one sequence per metric is allowed ({sequence.Metric}).", nameof(sequences));
            }
        }

        DeviceId = deviceId;
        _sequenceByMetric = sequenceByMetric;
    }

    /// <summary>
    /// Applies the sequence associated with the measurement's metric,
    /// when such a sequence exists.
    /// </summary>
    /// <remarks>
    /// Passes elapsed simulation time to the selected sequence unchanged.
    /// The sequence manages its own scenario timing and queue progression.
    /// Other metric sequences are not advanced by this call.
    /// Invoke this method once per new measurement; delivery retries
    /// must reuse the previously returned measurement.
    /// </remarks>
    /// <param name="measurement">
    /// The non-null measurement containing the normal value to process.
    /// It must originate from the device associated with this schedule.
    /// </param>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since the shared simulation start.
    /// Successive calls must supply nondecreasing values.
    /// </param>
    /// <returns>
    /// The measurement returned by the selected sequence,
    /// or the same original measurement instance when its metric
    /// has no sequence in this schedule.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="measurement"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="elapsed"/> is negative,
    /// including when the measurement's metric has no sequence.
    /// </exception>
    public SimulatorTelemetryMeasurementRequest Apply(SimulatorTelemetryMeasurementRequest measurement, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (!_sequenceByMetric.TryGetValue(measurement.Metric, out var sequence))
        {
            return measurement;
        }

        return sequence.Apply(measurement, elapsed);
    }
}
