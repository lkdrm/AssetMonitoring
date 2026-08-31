using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Tests.Support;

internal sealed class FakeTelemetryMeasurementRepository
    : ITelemetryMeasurementRepository
{
    private readonly HashSet<Guid> _measurementIds;
    private readonly List<TelemetryMeasurement> _pendingMeasurements = [];
    private readonly List<TelemetryMeasurement> _addedMeasurements = [];

    internal FakeTelemetryMeasurementRepository(
        IEnumerable<Guid>? existingMeasurementIds = null)
    {
        _measurementIds = existingMeasurementIds?.ToHashSet() ?? [];
    }

    internal IReadOnlyList<TelemetryMeasurement> AddedMeasurements =>
        _addedMeasurements;

    internal int ExistsCallCount { get; private set; }

    internal int SaveChangesCallCount { get; private set; }

    internal CancellationToken LastExistsCancellationToken { get; private set; }

    internal CancellationToken LastSaveCancellationToken { get; private set; }

    public Task<bool> ExistsAsync(
        Guid measurementId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ExistsCallCount++;
        LastExistsCancellationToken = cancellationToken;

        return Task.FromResult(_measurementIds.Contains(measurementId));
    }

    public void Add(TelemetryMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        _addedMeasurements.Add(measurement);
        _pendingMeasurements.Add(measurement);
    }

    public Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SaveChangesCallCount++;
        LastSaveCancellationToken = cancellationToken;

        foreach (var measurement in _pendingMeasurements)
        {
            _measurementIds.Add(measurement.Id);
        }

        _pendingMeasurements.Clear();

        return Task.CompletedTask;
    }
}
