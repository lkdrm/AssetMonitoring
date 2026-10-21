using AssetMonitoring.DeviceSimulator.Api.Contracts;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Stores metric scenario sequences and scheduled connectivity outages for one device.
/// </summary>
/// <remarks>
/// Each metric has its own stateful sequence. Calls to Apply must be sequential
/// and use nondecreasing elapsed simulation time.
/// Offline checks read immutable windows only and can be performed concurrently
/// by heartbeat and telemetry loops.
/// The caller decides whether to send data based on IsOffline; Apply only
/// transforms measurements and does not suppress sending.
/// </remarks>
public sealed class DeviceScenarioSchedule
{
    private readonly Dictionary<SimulatorTelemetryMetric, DeviceMetricScenarioSequence> _sequenceByMetric;
    private readonly IReadOnlyList<DeviceOfflineWindow> _deviceOfflineWindows;

    /// <summary>
    /// Gets the identifier of the device whose measurements
    /// are processed by this schedule.
    /// </summary>
    public Guid DeviceId { get; }

    /// <summary>
    /// Initializes a device schedule containing metric sequences
    /// and optional offline windows.
    /// </summary>
    /// <remarks>
    /// Copies the sequence mapping and the offline window collection.
    /// Sequence and window instances themselves are reused.
    /// Construction does not advance any measurement scenario.
    /// A schedule may contain only offline windows, only metric sequences,
    /// both, or neither.
    /// </remarks>
    /// <param name="deviceId">
    /// The nonempty identifier shared by every sequence and offline window.
    /// </param>
    /// <param name="sequences">
    /// The metric sequences. An empty collection is allowed.
    /// Items must be non-null, belong to the specified device,
    /// and handle distinct metrics.
    /// </param>
    /// <param name="deviceOfflineWindows">
    /// The offline windows, or null when none are configured.
    /// An empty collection is allowed. Items must be non-null and belong
    /// to the specified device. Overlapping windows are allowed.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when sequences is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device identifier is empty, either collection contains
    /// a null item or an item for another device, or multiple sequences
    /// handle the same metric.
    /// </exception>
    public DeviceScenarioSchedule(Guid deviceId, IEnumerable<DeviceMetricScenarioSequence> sequences, IReadOnlyList<DeviceOfflineWindow>? deviceOfflineWindows = null)
    {
        ArgumentNullException.ThrowIfNull(sequences);

        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        var deviceOfflineWindowsCheck = deviceOfflineWindows?.ToArray() ?? [];
        foreach (var deviceOffline in deviceOfflineWindowsCheck)
        {
            if (deviceOffline == null)
            {
                throw new ArgumentException("The offline window collection cannot contain null items.", nameof(deviceOfflineWindows));
            }

            if (deviceOffline.DeviceId != deviceId)
            {
                throw new ArgumentException("Every offline window must belong to the specified device.", nameof(deviceOfflineWindows));
            }
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
        _deviceOfflineWindows = deviceOfflineWindowsCheck;
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

    /// <summary>
    /// Determines whether any configured offline window is active
    /// at the specified elapsed simulation time.
    /// </summary>
    /// <remarks>
    /// Overlapping windows form one continuous offline period.
    /// Each window includes its start and excludes its end.
    /// This query does not advance metric sequences or retain execution state.
    /// </remarks>
    /// <param name="elapsed">
    /// The nonnegative elapsed time since simulation startup.
    /// </param>
    /// <returns>
    /// True when at least one window is active; otherwise, false.
    /// An empty window collection returns false.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when elapsed is negative, including when no windows exist.
    /// </exception>
    public bool IsOffline(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        return _deviceOfflineWindows.Any(window => window.IsActiveAt(elapsed));
    }
}
