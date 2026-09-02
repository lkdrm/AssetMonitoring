using AssetMonitoring.Modules.Telemetry.Application.History;
using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Telemetry;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Queries;

internal sealed class FakeTelemetryQueries : ITelemetryQueries
{
    private readonly TelemetryHistoryResult _result;
    private readonly TelemetryMeasurementResponse? _latestResult;

    internal FakeTelemetryQueries(
        TelemetryHistoryResult? result = null,
        TelemetryMeasurementResponse? latestResult = null)
    {
        _result = result ?? new TelemetryHistoryResult([], 1, 50, 0);
        _latestResult = latestResult;
    }

    internal Exception? ExceptionToThrow { get; set; }

    internal Exception? LatestExceptionToThrow { get; set; }

    internal int GetHistoryCallCount { get; private set; }

    internal TelemetryHistoryQuery? LastQuery { get; private set; }

    internal CancellationToken LastCancellationToken { get; private set; }

    internal int GetLatestCallCount { get; private set; }

    internal TelemetryLatestQuery? LastLatestQuery { get; private set; }

    internal CancellationToken LastLatestCancellationToken { get; private set; }

    public Task<TelemetryHistoryResult> GetHistoryAsync(
        TelemetryHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GetHistoryCallCount++;
        LastQuery = query;
        LastCancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(_result);
    }

    public Task<TelemetryMeasurementResponse?> GetLatestAsync(
        TelemetryLatestQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GetLatestCallCount++;
        LastLatestQuery = query;
        LastLatestCancellationToken = cancellationToken;

        if (LatestExceptionToThrow is not null)
        {
            throw LatestExceptionToThrow;
        }

        return Task.FromResult(_latestResult);
    }
}
