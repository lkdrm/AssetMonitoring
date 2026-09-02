using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Api.Contracts.Telemetry;

/// <summary>
/// Represents optional filters and pagination parameters for a telemetry
/// history HTTP request.
/// </summary>
public sealed class TelemetryHistoryRequest
{
    /// <summary>
    /// Gets or sets the optional telemetry metric filter.
    /// When null, measurements for all metrics are returned.
    /// </summary>
    public TelemetryMetric? Metric { get; set; }

    /// <summary>
    /// Gets or sets the optional inclusive UTC start of the measurement range.
    /// </summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>
    /// Gets or sets the optional inclusive UTC end of the measurement range.
    /// </summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>
    /// Gets or sets the one-based page number.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of measurements returned on one page.
    /// </summary>
    public int PageSize { get; set; } = 50;
}
