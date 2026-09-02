using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Contracts.Telemetry;

/// <summary>
/// Represents the query parameters used to request the latest telemetry
/// measurement.
/// </summary>
public sealed record TelemetryLatestRequest
{
    /// <summary>
    /// Gets or sets the required telemetry metric.
    /// </summary>
    public TelemetryMetric? Metric {  get; set; }
}
