namespace AssetMonitoring.Modules.Telemetry.Domain.Measurements;

/// <summary>
/// Represents a single telemetry measurement reported by a device.
/// </summary>
public sealed class TelemetryMeasurement
{
    /// <summary>
    /// Gets the identifier supplied by the telemetry message.
    /// It can be used to detect duplicate message delivery.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the identifier of the device that produced the measurement.
    /// </summary>
    public Guid DeviceId { get; private set; }

    /// <summary>
    /// Gets the metric represented by the measurement.
    /// </summary>
    public TelemetryMetric Metric { get; private set; }

    /// <summary>
    /// Gets the numeric value, or <see langword="null"/> when this is a
    /// state measurement.
    /// </summary>
    public double? NumericValue { get; private set; }

    /// <summary>
    /// Gets the boolean state value, or <see langword="null"/> when this is
    /// a numeric measurement.
    /// </summary>
    public bool? StateValue { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp at which the device produced the measurement.
    /// </summary>
    public DateTime MeasuredAtUtc { get; private set; }

    private TelemetryMeasurement()
    {
    }

    /// <summary>
    /// Creates a numeric telemetry measurement.
    /// </summary>
    /// <param name="id">
    /// The identifier supplied by the telemetry message.
    /// </param>
    /// <param name="deviceId">
    /// The identifier of the device that produced the measurement.
    /// </param>
    /// <param name="metric">
    /// The numeric metric, such as temperature or humidity.
    /// </param>
    /// <param name="value">The numeric measurement value.</param>
    /// <param name="measuredAtUtc">
    /// The UTC timestamp at which the measurement was produced.
    /// </param>
    /// <returns>A validated numeric telemetry measurement.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier is empty, the timestamp is not UTC, or
    /// <paramref name="metric"/> does not support numeric values.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is not finite or when humidity
    /// is outside the range from 0 to 100.
    /// </exception>
    public static TelemetryMeasurement CreateNumeric(Guid id, Guid deviceId, TelemetryMetric metric, double value, DateTime measuredAtUtc)
    {
        ValidateCommonData(id, deviceId, measuredAtUtc);

        if (metric is not TelemetryMetric.Temperature and not TelemetryMetric.Humidity)
        {
            throw new ArgumentException("The metric does not support numeric values.", nameof(metric));
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The numeric value must be finite.");
        }

        if (metric == TelemetryMetric.Humidity && value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Humidity must be between 0 and 100.");
        }

        return new TelemetryMeasurement { Id = id, DeviceId = deviceId, Metric = metric, NumericValue = value, StateValue = null, MeasuredAtUtc = measuredAtUtc };
    }

    /// <summary>
    /// Creates a boolean state telemetry measurement.
    /// </summary>
    /// <param name="id">
    /// The identifier supplied by the telemetry message.
    /// </param>
    /// <param name="deviceId">
    /// The identifier of the device that produced the measurement.
    /// </param>
    /// <param name="metric">
    /// The state metric, such as door state or light state.
    /// </param>
    /// <param name="value">The reported boolean state.</param>
    /// <param name="measuredAtUtc">
    /// The UTC timestamp at which the measurement was produced.
    /// </param>
    /// <returns>A validated state telemetry measurement.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when an identifier is empty, the timestamp is not UTC, or
    /// <paramref name="metric"/> does not support state values.
    /// </exception>
    public static TelemetryMeasurement CreateState(Guid id, Guid deviceId, TelemetryMetric metric, bool value, DateTime measuredAtUtc)
    {
        ValidateCommonData(id, deviceId, measuredAtUtc);

        if (metric is not TelemetryMetric.LightState and not TelemetryMetric.DoorState)
        {
            throw new ArgumentException("The metric does not support numeric values.", nameof(metric));
        }

        return new TelemetryMeasurement { Id = id, DeviceId = deviceId, Metric = metric, NumericValue = null, StateValue = value, MeasuredAtUtc = measuredAtUtc };
    }

    private static void ValidateCommonData(Guid id, Guid deviceId, DateTime measuredAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Measurement ID cannot be empty.", nameof(id));
        }
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        if (measuredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Measured time must be expressed in UTC.", nameof(measuredAtUtc));
        }
    }
}
