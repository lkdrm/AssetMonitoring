using AssetMonitoring.Modules.Telemetry.Domain.Measurements;

namespace AssetMonitoring.Modules.Telemetry.Tests.Domain.Measurements;

public sealed class TelemetryMeasurementTests
{
    private static readonly Guid MeasurementId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTime MeasuredAtUtc =
        new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateNumericTemperatureCreatesExpectedMeasurement()
    {
        const double value = -25.5;

        var measurement = TelemetryMeasurement.CreateNumeric(
            MeasurementId,
            DeviceId,
            TelemetryMetric.Temperature,
            value,
            MeasuredAtUtc);

        Assert.Equal(MeasurementId, measurement.Id);
        Assert.Equal(DeviceId, measurement.DeviceId);
        Assert.Equal(TelemetryMetric.Temperature, measurement.Metric);
        Assert.Equal(value, measurement.NumericValue);
        Assert.Null(measurement.StateValue);
        Assert.Equal(MeasuredAtUtc, measurement.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, measurement.MeasuredAtUtc.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void CreateNumericHumidityAcceptsBoundaryValues(double value)
    {
        var measurement = TelemetryMeasurement.CreateNumeric(
            MeasurementId,
            DeviceId,
            TelemetryMetric.Humidity,
            value,
            MeasuredAtUtc);

        Assert.Equal(TelemetryMetric.Humidity, measurement.Metric);
        Assert.Equal(value, measurement.NumericValue);
        Assert.Null(measurement.StateValue);
    }

    [Theory]
    [InlineData(TelemetryMetric.DoorState, true)]
    [InlineData(TelemetryMetric.LightState, false)]
    public void CreateStateCreatesExpectedMeasurement(
        TelemetryMetric metric,
        bool value)
    {
        var measurement = TelemetryMeasurement.CreateState(
            MeasurementId,
            DeviceId,
            metric,
            value,
            MeasuredAtUtc);

        Assert.Equal(MeasurementId, measurement.Id);
        Assert.Equal(DeviceId, measurement.DeviceId);
        Assert.Equal(metric, measurement.Metric);
        Assert.Null(measurement.NumericValue);
        Assert.Equal(value, measurement.StateValue);
        Assert.Equal(MeasuredAtUtc, measurement.MeasuredAtUtc);
    }

    [Fact]
    public void CreateNumericWithEmptyMeasurementIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateNumeric(
                Guid.Empty,
                DeviceId,
                TelemetryMetric.Temperature,
                20,
                MeasuredAtUtc));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void CreateStateWithEmptyDeviceIdThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateState(
                MeasurementId,
                Guid.Empty,
                TelemetryMetric.DoorState,
                true,
                MeasuredAtUtc));

        Assert.Equal("deviceId", exception.ParamName);
    }

    [Fact]
    public void CreateNumericWithNonUtcTimestampThrowsArgumentException()
    {
        var localTime = new DateTime(
            2026,
            8,
            29,
            12,
            0,
            0,
            DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateNumeric(
                MeasurementId,
                DeviceId,
                TelemetryMetric.Temperature,
                20,
                localTime));

        Assert.Equal("measuredAtUtc", exception.ParamName);
    }

    [Fact]
    public void CreateStateWithNonUtcTimestampThrowsArgumentException()
    {
        var unspecifiedTime = new DateTime(
            2026,
            8,
            29,
            12,
            0,
            0,
            DateTimeKind.Unspecified);

        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateState(
                MeasurementId,
                DeviceId,
                TelemetryMetric.LightState,
                false,
                unspecifiedTime));

        Assert.Equal("measuredAtUtc", exception.ParamName);
    }

    [Theory]
    [InlineData(TelemetryMetric.DoorState)]
    [InlineData(TelemetryMetric.LightState)]
    public void CreateNumericWithStateMetricThrowsArgumentException(
        TelemetryMetric metric)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateNumeric(
                MeasurementId,
                DeviceId,
                metric,
                1,
                MeasuredAtUtc));

        Assert.Equal("metric", exception.ParamName);
    }

    [Theory]
    [InlineData(TelemetryMetric.Temperature)]
    [InlineData(TelemetryMetric.Humidity)]
    public void CreateStateWithNumericMetricThrowsArgumentException(
        TelemetryMetric metric)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TelemetryMeasurement.CreateState(
                MeasurementId,
                DeviceId,
                metric,
                true,
                MeasuredAtUtc));

        Assert.Equal("metric", exception.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void CreateNumericWithNonFiniteValueThrowsArgumentOutOfRangeException(
        double value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TelemetryMeasurement.CreateNumeric(
                MeasurementId,
                DeviceId,
                TelemetryMetric.Temperature,
                value,
                MeasuredAtUtc));

        Assert.Equal("value", exception.ParamName);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void CreateNumericHumidityOutsideRangeThrowsArgumentOutOfRangeException(
        double value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TelemetryMeasurement.CreateNumeric(
                MeasurementId,
                DeviceId,
                TelemetryMetric.Humidity,
                value,
                MeasuredAtUtc));

        Assert.Equal("value", exception.ParamName);
    }
}
