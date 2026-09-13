using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies normal telemetry values, identifiers, UTC timestamps,
/// capability selection, warehouse time zones, and working-hour boundaries
/// without real-time waits.
/// </summary>
/// <remarks>
/// Requires Microsoft.Extensions.TimeProvider.Testing, also used by the
/// heartbeat tests. Fixed test time zones avoid operating-system dependencies.
/// Tests do not require particular random samples or an exact door-open ratio.
/// </remarks>
public sealed class NormalTelemetryGeneratorTests
{
    /// <summary>
    /// Provides a fixed instant with fractional seconds to detect lost precision.
    /// </summary>
    private static readonly DateTimeOffset InitialUtc =
        new DateTimeOffset(2026, 9, 11, 10, 20, 30, TimeSpan.Zero).AddTicks(1234567);

    /// <summary>
    /// Identifies every metric supported by the normal generator.
    /// </summary>
    private static readonly SimulatorTelemetryMetric[] Metrics =
    {
        SimulatorTelemetryMetric.Temperature,
        SimulatorTelemetryMetric.Humidity,
        SimulatorTelemetryMetric.DoorState,
        SimulatorTelemetryMetric.LightState
    };

    /// <summary>
    /// Verifies that the generator requires a time provider.
    /// </summary>
    [Fact]
    public void ConstructorWithNullTimeProviderThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new NormalTelemetryGenerator(null!));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    /// <summary>
    /// Verifies that repeated numeric measurements have the correct metric,
    /// finite values within the normal range, at most two decimal places,
    /// no state value, and the time supplied by the clock.
    /// </summary>
    /// <param name="metric">The numeric metric to generate.</param>
    /// <param name="minimum">The inclusive normal minimum.</param>
    /// <param name="maximum">The inclusive normal maximum.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature, 20.0, 24.0)]
    [InlineData(SimulatorTelemetryMetric.Humidity, 78.0, 82.0)]
    public void CreateNumericMeasurementReturnsValuesWithinNormalRange(
        SimulatorTelemetryMetric metric, double minimum, double maximum)
    {
        var clock = new FakeTimeProvider(InitialUtc);
        var generator = new NormalTelemetryGenerator(clock);

        for (var i = 0; i < 128; i++)
        {
            var measurement = GenerateMeasurement(generator, metric, TimeZoneInfo.Utc);

            AssertCommonFields(measurement, metric, InitialUtc);
            var value = Assert.IsType<double>(measurement.NumericValue);
            Assert.True(double.IsFinite(value));
            Assert.InRange(value, minimum, maximum);
            Assert.Equal(Math.Round(value, 2), value);
            Assert.Null(measurement.StateValue);
        }
    }

    /// <summary>
    /// Verifies state metric mapping and that the local schedule does not
    /// replace the UTC timestamp stored in the measurement.
    /// The daytime door state may be either true or false.
    /// </summary>
    /// <param name="metric">The state metric to generate.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void CreateStateMeasurementReturnsBooleanValueAndUtcTimestamp(
        SimulatorTelemetryMetric metric)
    {
        var clock = new FakeTimeProvider(InitialUtc);
        var generator = new NormalTelemetryGenerator(clock);
        var warehouseTimeZone = CreateWarehouseTimeZone(5.5);

        var measurement = GenerateMeasurement(generator, metric, warehouseTimeZone);

        AssertCommonFields(measurement, metric, InitialUtc);
        Assert.Null(measurement.NumericValue);
        Assert.IsType<bool>(measurement.StateValue);
    }

    /// <summary>
    /// Verifies that each method uses the current clock value on every call
    /// instead of caching the timestamp when the generator is constructed.
    /// </summary>
    /// <param name="metric">The metric to generate before and after advancing time.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void CreateMeasurementAfterClockAdvanceUsesUpdatedUtcTimestamp(
        SimulatorTelemetryMetric metric)
    {
        var clock = new FakeTimeProvider(InitialUtc);
        var generator = new NormalTelemetryGenerator(clock);
        var warehouseTimeZone = CreateWarehouseTimeZone(2);
        var elapsed = TimeSpan.FromSeconds(73);

        var first = GenerateMeasurement(generator, metric, warehouseTimeZone);
        clock.Advance(elapsed);
        var second = GenerateMeasurement(generator, metric, warehouseTimeZone);

        AssertCommonFields(first, metric, InitialUtc);
        AssertCommonFields(second, metric, InitialUtc.Add(elapsed));
        Assert.NotEqual(first.MeasurementId, second.MeasurementId);
    }

    /// <summary>
    /// Verifies that measurements generated at the same instant have distinct
    /// identifiers, both within one metric and across different metrics.
    /// </summary>
    [Fact]
    public void CreateMeasurementsAtSameInstantHaveDistinctIdentifiers()
    {
        var clock = new FakeTimeProvider(InitialUtc);
        var generator = new NormalTelemetryGenerator(clock);
        var identifiers = new HashSet<Guid>();

        for (var i = 0; i < 32; i++)
        {
            foreach (var metric in Metrics)
            {
                var measurement = GenerateMeasurement(generator, metric, TimeZoneInfo.Utc);

                AssertCommonFields(measurement, metric, InitialUtc);
                Assert.True(
                    identifiers.Add(measurement.MeasurementId),
                    "Each newly generated measurement must have a distinct identifier.");
            }
        }
    }

    /// <summary>
    /// Verifies that methods using warehouse working hours require a time zone.
    /// </summary>
    /// <param name="metric">The state metric whose time zone is missing.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void CreateStateMeasurementWithNullTimeZoneThrowsArgumentNullException(
        SimulatorTelemetryMetric metric)
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));

        var exception = Assert.Throws<ArgumentNullException>(() =>
            GenerateMeasurement(generator, metric, null!));

        Assert.Equal("warehouseTimeZone", exception.ParamName);
    }

    /// <summary>
    /// Verifies that lights follow warehouse local hours, including the exact
    /// opening and closing boundaries, fractional offsets, and midnight rollover.
    /// Daytime lights must always be on; they do not use door probabilities.
    /// </summary>
    /// <param name="utcText">The independently specified UTC instant.</param>
    /// <param name="offsetHours">The warehouse offset from UTC in hours.</param>
    /// <param name="expectedIsOn">Whether lights must be on at that local time.</param>
    [Theory]
    [InlineData("2026-09-11T05:59:59.9999999Z", 2.0, false)] // Local 07:59:59.9999999.
    [InlineData("2026-09-11T06:00:00Z", 2.0, true)]          // Local 08:00.
    [InlineData("2026-09-11T18:59:59.9999999Z", 2.0, true)]  // Local 20:59:59.9999999.
    [InlineData("2026-09-11T19:00:00Z", 2.0, false)]         // Local 21:00.
    [InlineData("2026-09-11T19:59:59.9999999Z", 2.0, false)] // Local 21:59:59.9999999.
    [InlineData("2026-09-11T22:00:00Z", 2.0, false)]         // Local midnight, next day.
    [InlineData("2026-09-11T12:00:00Z", -4.0, true)]        // Local 08:00.
    [InlineData("2026-09-12T00:59:59.9999999Z", -4.0, true)] // Local 20:59:59.9999999, previous day.
    [InlineData("2026-09-12T01:00:00Z", -4.0, false)]        // Local 21:00, previous day.
    [InlineData("2026-09-11T02:30:00Z", 5.5, true)]          // Local 08:00.
    public void CreateLightStateMeasurementFollowsWarehouseWorkingHours(
        string utcText, double offsetHours, bool expectedIsOn)
    {
        var utcNow = ParseUtc(utcText);
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(utcNow));
        var warehouseTimeZone = CreateWarehouseTimeZone(offsetHours);

        for (var i = 0; i < 32; i++)
        {
            var measurement = generator.CreateLightStateMeasurement(warehouseTimeZone);

            AssertCommonFields(measurement, SimulatorTelemetryMetric.LightState, utcNow);
            Assert.Null(measurement.NumericValue);
            Assert.Equal<bool?>(expectedIsOn, measurement.StateValue);
        }
    }

    /// <summary>
    /// Verifies that doors are always closed outside warehouse working hours.
    /// Repeated samples must all satisfy this deterministic rule, regardless
    /// of the random state that would be allowed during working hours.
    /// </summary>
    /// <param name="utcText">The independently specified UTC instant.</param>
    /// <param name="offsetHours">The warehouse offset from UTC in hours.</param>
    [Theory]
    [InlineData("2026-09-11T05:59:59.9999999Z", 2.0)] // Local 07:59:59.9999999.
    [InlineData("2026-09-11T19:00:00Z", 2.0)]         // Local 21:00.
    [InlineData("2026-09-11T19:59:59.9999999Z", 2.0)] // Local 21:59:59.9999999.
    [InlineData("2026-09-11T22:00:00Z", 2.0)]         // Local midnight, next day.
    [InlineData("2026-09-11T11:59:59.9999999Z", -4.0)] // Local 07:59:59.9999999.
    [InlineData("2026-09-12T01:00:00Z", -4.0)]        // Local 21:00, previous day.
    public void CreateDoorStateMeasurementOutsideWorkingHoursAlwaysReturnsClosed(
        string utcText, double offsetHours)
    {
        var utcNow = ParseUtc(utcText);
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(utcNow));
        var warehouseTimeZone = CreateWarehouseTimeZone(offsetHours);

        for (var i = 0; i < 64; i++)
        {
            var measurement = generator.CreateDoorStateMeasurement(warehouseTimeZone);

            AssertCommonFields(measurement, SimulatorTelemetryMetric.DoorState, utcNow);
            Assert.Null(measurement.NumericValue);
            Assert.Equal<bool?>(false, measurement.StateValue);
        }
    }

    /// <summary>
    /// Verifies that capability-based generation requires a device.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithNullDeviceThrowsArgumentNullException()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));

        var exception = Assert.Throws<ArgumentNullException>(() =>
            generator.CreateMeasurements(null!, TimeZoneInfo.Utc));

        Assert.Equal("device", exception.ParamName);
    }

    /// <summary>
    /// Verifies that the batch contract requires a warehouse time zone,
    /// even when the supplied device has only a numeric capability.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithNullTimeZoneThrowsArgumentNullException()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));
        var device = CreateDevice(SimulatorDeviceCapability.Temperature);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            generator.CreateMeasurements(device, null!));

        Assert.Equal("warehouseTimeZone", exception.ParamName);
    }

    /// <summary>
    /// Verifies that an existing device with a missing capabilities collection
    /// is reported as an invalid device argument.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithNullCapabilitiesThrowsArgumentException()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));
        var device = CreateDevice() with { Capabilities = null! };

        var exception = Assert.Throws<ArgumentException>(() =>
            generator.CreateMeasurements(device, TimeZoneInfo.Utc));

        Assert.Equal("device", exception.ParamName);
    }

    /// <summary>
    /// Verifies that a device without capabilities produces an empty collection.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithNoCapabilitiesReturnsEmptyCollection()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));

        var measurements = generator.CreateMeasurements(CreateDevice(), TimeZoneInfo.Utc);

        Assert.NotNull(measurements);
        Assert.Empty(measurements);
    }

    /// <summary>
    /// Verifies the explicit capability-to-metric mapping and that a device
    /// with one capability receives exactly one measurement.
    /// </summary>
    /// <param name="capability">The capability advertised by the device.</param>
    /// <param name="expectedMetric">The independently specified expected metric.</param>
    [Theory]
    [InlineData(SimulatorDeviceCapability.Temperature, SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorDeviceCapability.Humidity, SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorDeviceCapability.DoorState, SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorDeviceCapability.LightState, SimulatorTelemetryMetric.LightState)]
    public void CreateMeasurementsWithSingleCapabilityReturnsMatchingMeasurement(
        SimulatorDeviceCapability capability, SimulatorTelemetryMetric expectedMetric)
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));

        var measurements = generator.CreateMeasurements(CreateDevice(capability), TimeZoneInfo.Utc);

        var measurement = Assert.Single(measurements);
        AssertCommonFields(measurement, expectedMetric, InitialUtc);
    }

    /// <summary>
    /// Verifies that generation processes the complete capabilities collection
    /// and produces all supported metrics with distinct measurement identifiers.
    /// The result order is not part of this assertion.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithAllCapabilitiesReturnsEveryMetric()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));
        var device = CreateDevice(
            SimulatorDeviceCapability.LightState,
            SimulatorDeviceCapability.Temperature,
            SimulatorDeviceCapability.DoorState,
            SimulatorDeviceCapability.Humidity);

        var measurements = generator.CreateMeasurements(device, TimeZoneInfo.Utc);

        Assert.Equal(4, measurements.Count);
        Assert.Equal(
            Metrics.OrderBy(metric => metric),
            measurements.Select(measurement => measurement.Metric).OrderBy(metric => metric));
        Assert.Equal(4, measurements.Select(measurement => measurement.MeasurementId).Distinct().Count());
        foreach (var measurement in measurements)
        {
            Assert.NotEqual(Guid.Empty, measurement.MeasurementId);
            Assert.Equal(InitialUtc.UtcDateTime, measurement.MeasuredAtUtc);
            Assert.Equal(DateTimeKind.Utc, measurement.MeasuredAtUtc.Kind);
        }
    }

    /// <summary>
    /// Verifies that duplicate capabilities produce one measurement per metric
    /// while preserving the caller's capabilities collection.
    /// </summary>
    [Fact]
    public void CreateMeasurementsWithDuplicateCapabilitiesReturnsDistinctMetrics()
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));
        var capabilities = new[]
        {
            SimulatorDeviceCapability.Humidity,
            SimulatorDeviceCapability.LightState,
            SimulatorDeviceCapability.Humidity,
            SimulatorDeviceCapability.LightState,
            SimulatorDeviceCapability.Humidity
        };
        var originalCapabilities = capabilities.ToArray();
        var device = CreateDevice(capabilities);

        var measurements = generator.CreateMeasurements(device, TimeZoneInfo.Utc);

        Assert.Equal(2, measurements.Count);
        Assert.Single(measurements.Where(measurement => measurement.Metric == SimulatorTelemetryMetric.Humidity));
        Assert.Single(measurements.Where(measurement => measurement.Metric == SimulatorTelemetryMetric.LightState));
        Assert.Equal<SimulatorDeviceCapability>(originalCapabilities, device.Capabilities);
    }

    /// <summary>
    /// Verifies that an unknown capability causes an exception that identifies
    /// the unsupported value, even when a valid capability precedes it.
    /// </summary>
    /// <param name="capabilityValue">An undefined capability value.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void CreateMeasurementsWithUnsupportedCapabilityThrowsInvalidOperationException(
        int capabilityValue)
    {
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(InitialUtc));
        var capability = (SimulatorDeviceCapability)capabilityValue;
        var device = CreateDevice(SimulatorDeviceCapability.Temperature, capability);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.CreateMeasurements(device, TimeZoneInfo.Utc));

        Assert.Contains(capability.ToString(), exception.Message);
    }

    /// <summary>
    /// Verifies that batch generation passes the supplied warehouse time zone
    /// to both state methods while keeping measurement timestamps in UTC.
    /// Outside working hours, every sampled door state must be closed.
    /// </summary>
    /// <param name="offsetHours">The warehouse's fixed UTC offset.</param>
    /// <param name="expectedLightIsOn">The expected light state in that time zone.</param>
    [Theory]
    [InlineData(3.0, false)] // UTC 18:30 is local 21:30, after closing.
    [InlineData(-4.0, true)] // UTC 18:30 is local 14:30, during working hours.
    public void CreateMeasurementsUsesWarehouseTimeZoneForStateMetrics(
        double offsetHours, bool expectedLightIsOn)
    {
        var utcNow = ParseUtc("2026-09-11T18:30:00Z");
        var generator = new NormalTelemetryGenerator(new FakeTimeProvider(utcNow));
        var warehouseTimeZone = CreateWarehouseTimeZone(offsetHours);
        var device = CreateDevice(
            SimulatorDeviceCapability.DoorState,
            SimulatorDeviceCapability.LightState);

        for (var i = 0; i < 64; i++)
        {
            var measurements = generator.CreateMeasurements(device, warehouseTimeZone);

            Assert.Equal(2, measurements.Count);
            var door = Assert.Single(measurements.Where(
                measurement => measurement.Metric == SimulatorTelemetryMetric.DoorState));
            var light = Assert.Single(measurements.Where(
                measurement => measurement.Metric == SimulatorTelemetryMetric.LightState));

            AssertCommonFields(door, SimulatorTelemetryMetric.DoorState, utcNow);
            AssertCommonFields(light, SimulatorTelemetryMetric.LightState, utcNow);
            Assert.Null(door.NumericValue);
            Assert.Null(light.NumericValue);
            Assert.IsType<bool>(door.StateValue);
            Assert.Equal<bool?>(expectedLightIsOn, light.StateValue);
            if (!expectedLightIsOn)
            {
                Assert.Equal<bool?>(false, door.StateValue);
            }
        }
    }

    /// <summary>
    /// Creates an active device with precisely the capabilities supplied by a test.
    /// </summary>
    /// <param name="capabilities">The device's capabilities, which may be empty.</param>
    /// <returns>A simulator device response without making an API request.</returns>
    private static SimulatorDeviceResponse CreateDevice(params SimulatorDeviceCapability[] capabilities)
    {
        return new SimulatorDeviceResponse(
            Guid.Parse("d5328b8a-101b-4f99-a1fd-b0bd387a9e59"),
            "WH-001",
            "Warehouse Test Device",
            capabilities,
            SimulatorDeviceLifecycle.Active);
    }

    /// <summary>
    /// Invokes the production method associated with a test metric.
    /// This helper does not reproduce generation or scheduling logic.
    /// </summary>
    /// <param name="generator">The generator under test.</param>
    /// <param name="metric">The method to invoke.</param>
    /// <param name="warehouseTimeZone">The time zone passed to state methods.</param>
    /// <returns>The generated measurement.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the test requests an unsupported metric.
    /// </exception>
    private static SimulatorTelemetryMeasurementRequest GenerateMeasurement(
        NormalTelemetryGenerator generator,
        SimulatorTelemetryMetric metric,
        TimeZoneInfo warehouseTimeZone)
    {
        return metric switch
        {
            SimulatorTelemetryMetric.Temperature => generator.CreateTemperatureMeasurement(),
            SimulatorTelemetryMetric.Humidity => generator.CreateHumidityMeasurement(),
            SimulatorTelemetryMetric.DoorState => generator.CreateDoorStateMeasurement(warehouseTimeZone),
            SimulatorTelemetryMetric.LightState => generator.CreateLightStateMeasurement(warehouseTimeZone),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unsupported test metric.")
        };
    }

    /// <summary>
    /// Verifies metadata shared by numeric and state measurements.
    /// </summary>
    /// <param name="measurement">The generated measurement.</param>
    /// <param name="expectedMetric">The metric requested by the test.</param>
    /// <param name="expectedUtc">The clock instant that should be preserved.</param>
    private static void AssertCommonFields(
        SimulatorTelemetryMeasurementRequest measurement,
        SimulatorTelemetryMetric expectedMetric,
        DateTimeOffset expectedUtc)
    {
        Assert.NotNull(measurement);
        Assert.NotEqual(Guid.Empty, measurement.MeasurementId);
        Assert.Equal(expectedMetric, measurement.Metric);
        Assert.Equal(expectedUtc.UtcDateTime, measurement.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, measurement.MeasuredAtUtc.Kind);
    }

    /// <summary>
    /// Creates a fixed warehouse time zone without looking up installed zones.
    /// </summary>
    /// <param name="offsetHours">The fixed offset from UTC in hours.</param>
    /// <returns>A test time zone with no daylight-saving adjustments.</returns>
    private static TimeZoneInfo CreateWarehouseTimeZone(double offsetHours)
    {
        return TimeZoneInfo.CreateCustomTimeZone(
            "WarehouseTestZone",
            TimeSpan.FromHours(offsetHours),
            "Warehouse test time",
            "Warehouse test time");
    }

    /// <summary>
    /// Parses an explicit UTC fixture independently of the machine's culture.
    /// </summary>
    /// <param name="value">An ISO timestamp with a UTC designator.</param>
    /// <returns>The fixture instant expressed in UTC.</returns>
    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
