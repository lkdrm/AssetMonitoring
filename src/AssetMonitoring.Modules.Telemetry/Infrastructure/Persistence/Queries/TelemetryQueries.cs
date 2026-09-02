using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;
using AssetMonitoring.Modules.Telemetry.Database;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Queries;

/// <summary>
/// Provides read-only EF Core queries for telemetry measurements.
/// </summary>
internal sealed class TelemetryQueries : ITelemetryQueries
{
    private readonly TelemetryDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryQueries"/> class.
    /// </summary>
    /// <param name="dbContext">
    /// The Telemetry database context used to execute read-only queries.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="dbContext"/> is null.
    /// </exception>
    public TelemetryQueries(TelemetryDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext, nameof(dbContext));

        _dbContext = dbContext;
    }

    ///<inheritdoc/>
    public async Task<TelemetryHistoryResult> GetHistoryAsync(TelemetryHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);

        var databaseQuery = _dbContext.Measurements.AsNoTracking().Where(measurement => measurement.DeviceId.Equals(query.DeviceId));

        if (query.Metric.HasValue)
        {
            databaseQuery = databaseQuery.Where(measurement => measurement.Metric == query.Metric.Value);
        }

        if (query.FromUtc.HasValue)
        {
            databaseQuery = databaseQuery.Where(measurement => measurement.MeasuredAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            databaseQuery = databaseQuery.Where(measurement => measurement.MeasuredAtUtc <= query.ToUtc.Value);
        }

        var totalCount = await databaseQuery.CountAsync(cancellationToken);

        var items = await databaseQuery
            .OrderByDescending(measurement => measurement.MeasuredAtUtc)
            .ThenByDescending(measurement => measurement.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(device => new TelemetryMeasurementResponse(
                device.Id,
                device.DeviceId,
                device.Metric,
                device.NumericValue,
                device.StateValue,
                device.MeasuredAtUtc)).ToListAsync(cancellationToken);


        return new TelemetryHistoryResult(items, query.Page, query.PageSize, totalCount);
    }

    /// <inheritdoc />
    public async Task<TelemetryMeasurementResponse?> GetLatestAsync(TelemetryLatestQuery query, CancellationToken cancellationToken = default)
    {
        ValidateLatestQuery(query);

        var latestMeasurement = await _dbContext.Measurements.AsNoTracking()
            .Where(device => device.DeviceId == query.DeviceId)
            .Where(metric => metric.Metric == query.Metric)
            .OrderByDescending(measurement => measurement.MeasuredAtUtc)
            .ThenByDescending(measurement => measurement.Id)
            .Select(device => new TelemetryMeasurementResponse(
                device.Id,
                device.DeviceId,
                device.Metric,
                device.NumericValue,
                device.StateValue,
                device.MeasuredAtUtc)).FirstOrDefaultAsync(cancellationToken);

        return latestMeasurement;
    }

    private static void ValidateQuery(TelemetryHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.DeviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(query.DeviceId));
        }

        if (query.Metric.HasValue && !Enum.IsDefined(query.Metric.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(query.Metric), query.Metric, "Unsupported telemetry metric.");
        }

        if (query.FromUtc.HasValue && query.FromUtc.Value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The start timestamp must be expressed in UTC.", nameof(query.FromUtc));
        }

        if (query.ToUtc.HasValue && query.ToUtc.Value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The end timestamp must be expressed in UTC.", nameof(query.ToUtc));
        }

        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
        {
            throw new ArgumentException("The start timestamp cannot be later than the end timestamp.", nameof(query));
        }

        if (query.Page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query.Page), query.Page, "Page number must be at least 1.");
        }

        if (query.PageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), query.PageSize, "Page size must be between 1 and 200.");
        }
    }

    private static void ValidateLatestQuery(TelemetryLatestQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.DeviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(query.DeviceId));
        }

        if (!Enum.IsDefined(query.Metric))
        {
            throw new ArgumentOutOfRangeException(nameof(query.Metric), query.Metric, "Unsupported telemetry metric.");
        }
    }
}
