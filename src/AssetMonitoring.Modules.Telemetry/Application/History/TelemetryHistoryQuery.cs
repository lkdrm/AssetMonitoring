using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.History;

/// <summary>
/// Defines filters and pagination for a telemetry measurement history query.
/// </summary>
/// <param name="DeviceId">
/// The identifier of the device whose telemetry history is requested.
/// </param>
/// <param name="Metric">
/// The optional telemetry metric filter. When null, all metrics are returned.
/// </param>
/// <param name="FromUtc">
/// The optional inclusive UTC start of the measurement time range.
/// </param>
/// <param name="ToUtc">
/// The optional inclusive UTC end of the measurement time range.
/// </param>
/// <param name="Page">
/// The one-based page number.
/// </param>
/// <param name="PageSize">
/// The maximum number of measurements returned on one page.
/// </param>
public sealed record TelemetryHistoryQuery(Guid DeviceId, TelemetryMetric? Metric = null, DateTime? FromUtc = null, DateTime? ToUtc = null, int Page = 1, int PageSize = 50);
