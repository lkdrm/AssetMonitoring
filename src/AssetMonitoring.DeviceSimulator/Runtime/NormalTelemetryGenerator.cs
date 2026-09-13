using AssetMonitoring.DeviceSimulator.Api.Contracts;

namespace AssetMonitoring.DeviceSimulator.Runtime;

/// <summary>
/// Generates telemetry measurements representing normal device operation.
/// </summary>
public sealed class NormalTelemetryGenerator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="NormalTelemetryGenerator"/> class.
    /// </summary>
    /// <param name="timeProvider">
    /// The time provider used to timestamp generated measurements.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="timeProvider"/> is null.
    /// </exception>
    public NormalTelemetryGenerator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates a normal temperature measurement between 20 and 24 degrees Celsius.
    /// </summary>
    /// <returns>
    /// A temperature measurement with a new identifier, a numeric value,
    /// a null state value, and the current UTC timestamp.
    /// </returns>
    public SimulatorTelemetryMeasurementRequest CreateTemperatureMeasurement()
    {
        var temperature = Math.Round(20 + Random.Shared.NextDouble() * (24 - 20), 2);
        return new SimulatorTelemetryMeasurementRequest(Guid.NewGuid(), SimulatorTelemetryMetric.Temperature, temperature, null, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Creates a normal relative humidity measurement between 78 and 82 percent.
    /// </summary>
    /// <returns>
    /// A humidity measurement with a new identifier, a numeric value,
    /// a null state value, and the current UTC timestamp.
    /// </returns>
    public SimulatorTelemetryMeasurementRequest CreateHumidityMeasurement()
    {
        var humidity = Math.Round(78 + Random.Shared.NextDouble() * (82 - 78), 2);
        return new SimulatorTelemetryMeasurementRequest(Guid.NewGuid(), SimulatorTelemetryMetric.Humidity, humidity, null, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Creates a normal door state measurement using warehouse local time.
    /// </summary>
    /// <remarks>
    /// Doors are closed outside the interval from 08:00 inclusive to 21:00 exclusive.
    /// During working hours, each measurement has a 20 percent probability
    /// of reporting an open door.
    /// </remarks>
    /// <param name="warehouseTimeZone">
    /// The time zone used to evaluate warehouse working hours.
    /// </param>
    /// <returns>
    /// A door state measurement with a new identifier, a null numeric value,
    /// a boolean state value, and the current UTC timestamp.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="warehouseTimeZone"/> is null.
    /// </exception>
    public SimulatorTelemetryMeasurementRequest CreateDoorStateMeasurement(TimeZoneInfo warehouseTimeZone)
    {
        ArgumentNullException.ThrowIfNull(warehouseTimeZone);

        var utcNow = _timeProvider.GetUtcNow();
        var warehouseNow = TimeZoneInfo.ConvertTime(utcNow, warehouseTimeZone);

        var isWorkingHours = warehouseNow.Hour >= 8 && warehouseNow.Hour < 21;
        var isOpen = isWorkingHours && Random.Shared.NextDouble() < 0.2;

        return new SimulatorTelemetryMeasurementRequest(Guid.NewGuid(), SimulatorTelemetryMetric.DoorState, null, isOpen, utcNow.UtcDateTime);
    }

    /// <summary>
    /// Creates a normal light state measurement using warehouse local time.
    /// </summary>
    /// <remarks>
    /// Lights are on from 08:00 inclusive to 21:00 exclusive
    /// and off outside that interval.
    /// </remarks>
    /// <param name="warehouseTimeZone">
    /// The time zone used to evaluate warehouse working hours.
    /// </param>
    /// <returns>
    /// A light state measurement with a new identifier, a null numeric value,
    /// a boolean state value, and the current UTC timestamp.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="warehouseTimeZone"/> is null.
    /// </exception>
    public SimulatorTelemetryMeasurementRequest CreateLightStateMeasurement(TimeZoneInfo warehouseTimeZone)
    {
        ArgumentNullException.ThrowIfNull(warehouseTimeZone);

        var utcNow = _timeProvider.GetUtcNow();
        var warehouseNow = TimeZoneInfo.ConvertTime(utcNow, warehouseTimeZone);

        var isOn = warehouseNow.Hour >= 8 && warehouseNow.Hour < 21;

        return new SimulatorTelemetryMeasurementRequest(Guid.NewGuid(), SimulatorTelemetryMetric.LightState, null, isOn, utcNow.UtcDateTime);
    }

    /// <summary>
    /// Creates normal telemetry measurements for the device's supported capabilities.
    /// </summary>
    /// <param name="device">
    /// The device whose capabilities determine which measurements are generated.
    /// </param>
    /// <param name="warehouseTimeZone">
    /// The time zone used to generate door and light states.
    /// </param>
    /// <returns>
    /// One measurement per distinct supported capability.
    /// An empty collection when the device has no capabilities.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/> or
    /// <paramref name="warehouseTimeZone"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the device capabilities collection is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device contains an unsupported capability.
    /// </exception>
    public IReadOnlyList<SimulatorTelemetryMeasurementRequest> CreateMeasurements(SimulatorDeviceResponse device, TimeZoneInfo warehouseTimeZone)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(warehouseTimeZone);

        if (device.Capabilities == null)
        {
            throw new ArgumentException("Device capabilities must be provided.", nameof(device));
        }

        var measurements = new List<SimulatorTelemetryMeasurementRequest>();

        foreach (var capability in device.Capabilities.Distinct())
        {
            switch (capability)
            {
                case SimulatorDeviceCapability.Temperature:
                    measurements.Add(CreateTemperatureMeasurement());
                    break;
                case SimulatorDeviceCapability.Humidity:
                    measurements.Add(CreateHumidityMeasurement());
                    break;
                case SimulatorDeviceCapability.DoorState:
                    measurements.Add(CreateDoorStateMeasurement(warehouseTimeZone));
                    break;
                case SimulatorDeviceCapability.LightState:
                    measurements.Add(CreateLightStateMeasurement(warehouseTimeZone));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported device capability '{capability}'.");
            }
        }

        return measurements;
    }
}
