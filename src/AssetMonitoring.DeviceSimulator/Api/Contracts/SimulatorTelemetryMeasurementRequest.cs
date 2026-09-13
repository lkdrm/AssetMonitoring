namespace AssetMonitoring.DeviceSimulator.Api.Contracts;

/// <summary>
/// Represents a request to record a simulated telemetry measurement.
/// </summary>
/// <param name="MeasurementId">
/// The unique measurement identifier, preserved when retrying
/// the same measurement.
/// </param>
/// <param name="Metric">
/// The type of telemetry measurement.
/// </param>
/// <param name="NumericValue">
/// The numeric value for temperature or humidity;
/// otherwise, null.
/// </param>
/// <param name="StateValue">
/// The boolean value for door or light state;
/// otherwise, null.
/// </param>
/// <param name="MeasuredAtUtc">
/// The UTC timestamp at which the measurement was captured.
/// </param>
public sealed record SimulatorTelemetryMeasurementRequest(Guid MeasurementId, SimulatorTelemetryMetric Metric, double? NumericValue, bool? StateValue, DateTime MeasuredAtUtc);
