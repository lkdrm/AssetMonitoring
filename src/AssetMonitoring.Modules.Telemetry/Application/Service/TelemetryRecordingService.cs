using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Recording;
using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Application.Service;

/// <summary>
/// Coordinates validation and idempotent persistence of telemetry measurements.
/// </summary>
public sealed class TelemetryRecordingService
{
    private readonly ITelemetryMeasurementRepository _measurementRepository;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TelemetryRecordingService"/> class.
    /// </summary>
    /// <param name="measurementRepository">
    /// The repository used to query and persist telemetry measurements.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="measurementRepository"/> is null.
    /// </exception>
    public TelemetryRecordingService(ITelemetryMeasurementRepository measurementRepository)
    {
        ArgumentNullException.ThrowIfNull(measurementRepository, nameof(measurementRepository));
        _measurementRepository = measurementRepository;
    }

    /// <summary>
    /// Asynchronously records a telemetry measurement.
    /// Repeated measurement identifiers are handled idempotently.
    /// </summary>
    /// <param name="request">
    /// The telemetry measurement recording request.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous operation.
    /// </param>
    /// <returns>
    /// A result indicating whether a new measurement was persisted.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the request contains invalid measurement data.
    /// </exception>
    public async Task<TelemetryRecordingResult> RecordAsync(TelemetryRecordingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var exists = await _measurementRepository.ExistsAsync(request.MeasurementId, cancellationToken);

        if (exists)
        {
            return new TelemetryRecordingResult(request.MeasurementId, false);
        }

        var measurement = CreateMeasurement(request);

        _measurementRepository.Add(measurement);
        await _measurementRepository.SaveChangesAsync(cancellationToken);

        return new TelemetryRecordingResult(request.MeasurementId, true);
    }

    private static TelemetryMeasurement CreateMeasurement(TelemetryRecordingRequest request)
    {
        TelemetryMeasurement measurement;
        switch (request.Metric)
        {
            case TelemetryMetric.Humidity or TelemetryMetric.Temperature:
                if (request.NumericValue is null)
                {
                    throw new ArgumentException("Numeric telemetry metrics require a numeric value.", nameof(request));
                }

                if (request.StateValue is not null)
                {
                    throw new ArgumentException("Numeric telemetry metrics cannot contain a state value.", nameof(request));
                }

                measurement = TelemetryMeasurement.CreateNumeric(request.MeasurementId, request.DeviceId, request.Metric, request.NumericValue.Value, request.MeasuredAtUtc);
                break;

            case TelemetryMetric.DoorState or TelemetryMetric.LightState:
                if (request.StateValue is null)
                {
                    throw new ArgumentException("State telemetry metrics require a state value.", nameof(request));
                }

                if (request.NumericValue is not null)
                {
                    throw new ArgumentException("State telemetry metrics cannot contain a numeric value.", nameof(request));
                }

                measurement = TelemetryMeasurement.CreateState(request.MeasurementId, request.DeviceId, request.Metric, request.StateValue.Value, request.MeasuredAtUtc);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Metric, "Unsupported telemetry metric.");
        }

        return measurement;
    }
}
