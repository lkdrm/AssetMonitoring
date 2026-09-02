using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.Telemetry;

/// <summary>
/// Represents one telemetry measurement returned by a read-only query.
/// </summary>
/// <param name="MeasurementId">
/// The unique measurement identifier.
/// </param>
/// <param name="DeviceId">
/// The identifier of the device that produced the measurement.
/// </param>
/// <param name="Metric">
/// The type of telemetry metric.
/// </param>
/// <param name="NumericValue">
/// The numeric value for temperature or humidity measurements; otherwise, null.
/// </param>
/// <param name="StateValue">
/// The state value for door or light measurements; otherwise, null.
/// </param>
/// <param name="MeasuredAtUtc">
/// The UTC timestamp at which the measurement was produced.
/// </param>
public sealed record TelemetryMeasurementResponse(Guid MeasurementId, Guid DeviceId, TelemetryMetric Metric, double? NumericValue, bool? StateValue, DateTime MeasuredAtUtc);
