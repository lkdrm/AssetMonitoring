using AssetMonitoring.Modules.Telemetry.Application.Telemetry;

namespace AssetMonitoring.Modules.Telemetry.Application.History;

/// <summary>
/// Represents a page of telemetry measurement history.
/// </summary>
/// <param name="Items">
/// The telemetry measurements contained in the requested page.
/// </param>
/// <param name="Page">
/// The current one-based page number.
/// </param>
/// <param name="PageSize">
/// The maximum number of measurements requested for the page.
/// </param>
/// <param name="TotalCount">
/// The total number of measurements matching the query before pagination.
/// </param>
public sealed record TelemetryHistoryResult(IReadOnlyList<TelemetryMeasurementResponse> Items, int Page, int PageSize, int TotalCount);
