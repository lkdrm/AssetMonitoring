using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.History;

/// <summary>
/// Defines the device and metric used to query the latest telemetry measurement.
/// </summary>
/// <param name="DeviceId">
/// The identifier of the device whose latest measurement is requested.
/// </param>
/// <param name="Metric">
/// The required telemetry metric.
/// </param>
public sealed record TelemetryLatestQuery(Guid DeviceId, TelemetryMetric Metric);
