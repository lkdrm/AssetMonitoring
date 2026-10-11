using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies numeric continuity when fresh temperature and humidity runtimes
/// start inside a metric sequence.
/// </summary>
/// <remarks>
/// Uses real runtimes and a fixed random sample. Tests supply valid numeric
/// measurements directly, without HTTP requests or real-time delays.
/// Add this file alongside the existing scenario and runner tests.
/// </remarks>
public sealed class DeviceMetricScenarioSequenceFirstMeasurementTests
{
    private static readonly Guid DeviceId = Guid.Parse("5cba476d-cd91-474c-bab7-52480b9664b7");
    private static readonly Guid OtherDeviceId = Guid.Parse("35be79d2-a143-4229-83ab-1467814ab937");
    private static readonly DateTime InitialUtc = new(2026, 10, 11, 19, 0, 0, DateTimeKind.Utc);
    private const double MaximumRise = 3;

    /// <summary>
    /// Reproduces the observed first-step defect when the newly generated
    /// normal value is higher than the previous sequence output.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAtScenarioStartLimitsRiseFromPreviousOutput(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        var pendingMeasurement = CreateMeasurement(metric, baseline + 0.31);

        Assert.Same(pendingMeasurement, sequence.Apply(pendingMeasurement, TimeSpan.FromSeconds(55)));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(GetCurrentValue(runtime));

        var activeMeasurement = CreateMeasurement(metric, baseline + 0.77);
        var result = sequence.Apply(activeMeasurement, TimeSpan.FromMinutes(1));

        AssertNumericValue(result, baseline + 3.31);
        Assert.InRange(GetNumericValue(result) - GetNumericValue(pendingMeasurement), 0, MaximumRise + 1e-10);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>
    /// Verifies that a lower new normal value does not become the starting
    /// baseline and reduce the first rise from the preceding output.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAtScenarioStartUsesPreviousOutputWhenNormalValueFalls(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, baseline + 2), TimeSpan.Zero);

        var result = sequence.Apply(CreateMeasurement(metric, baseline - 2), TimeSpan.FromMinutes(1));

        AssertNumericValue(result, baseline + 5);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>
    /// Verifies that the latest pending output replaces older pending values
    /// while the runtime itself remains unstarted.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAtScenarioStartUsesMostRecentPendingMeasurement(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        var pendingMeasurements = new[]
        {
            CreateMeasurement(metric, baseline + 1),
            CreateMeasurement(metric, baseline - 0.5),
            CreateMeasurement(metric, baseline + 0.2)
        };
        var times = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(59) };

        for (var index = 0; index < pendingMeasurements.Length; index++)
        {
            Assert.Same(pendingMeasurements[index], sequence.Apply(pendingMeasurements[index], times[index]));
            Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
            Assert.Null(GetCurrentValue(runtime));
        }

        var result = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1));

        AssertNumericValue(result, baseline + 3.2);
    }

    /// <summary>
    /// Verifies the original initial-value behavior when a scenario starts
    /// immediately and no preceding sequence output exists.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWithoutPreviousOutputUsesCurrentMeasurement(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric, startsAfter: TimeSpan.Zero);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        var measurement = CreateMeasurement(metric, baseline + 0.77);

        var result = sequence.Apply(measurement, TimeSpan.Zero);

        AssertNumericValue(result, baseline + 3.77);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
        AssertNumericValue(measurement, baseline + 0.77);
    }

    /// <summary>
    /// Verifies that successive active measurements continue from the runtime's
    /// saved value despite changes in newly generated normal values.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAfterScenarioStartContinuesFromRuntimeValue(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, baseline), TimeSpan.Zero);

        var first = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1));
        var second = sequence.Apply(CreateMeasurement(metric, baseline - 1), TimeSpan.FromSeconds(65));
        var third = sequence.Apply(CreateMeasurement(metric, baseline + 0.5), TimeSpan.FromSeconds(70));

        AssertNumericValue(first, baseline + 3);
        AssertNumericValue(second, baseline + 6);
        AssertNumericValue(third, baseline + 9);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>
    /// Verifies that selecting the preceding output as the baseline still
    /// respects the runtime's fixed abnormal target.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAtScenarioStartDoesNotOvershootSelectedTarget(SimulatorTelemetryMetric metric)
    {
        var target = GetNormalBaseline(metric) + 12;
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, target - 0.25), TimeSpan.Zero);

        var result = sequence.Apply(CreateMeasurement(metric, target - 5), TimeSpan.FromMinutes(1));

        AssertNumericValue(result, target);
        Assert.InRange(GetNumericValue(result) - (target - 0.25), 0, MaximumRise);
    }

    /// <summary>
    /// Verifies that recovery captures the current normal measurement rather
    /// than the last abnormal output saved by the sequence.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyDuringRecoveryCapturesCurrentNormalValue(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, baseline), TimeSpan.Zero);
        AssertNumericValue(sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1)), baseline + 3);
        AssertNumericValue(sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromSeconds(65)), baseline + 6);

        var recovering = sequence.Apply(CreateMeasurement(metric, baseline - 2), TimeSpan.FromMinutes(2));
        AssertNumericValue(recovering, baseline + 6);
        Assert.Equal(ScenarioPhase.Recovering, runtime.Phase);

        var completed = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(3));
        AssertNumericValue(completed, baseline - 2);
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
        Assert.Null(sequence.CurrentScenario);

        var normalMeasurement = CreateMeasurement(metric, baseline - 1);
        Assert.Same(normalMeasurement, sequence.Apply(normalMeasurement, TimeSpan.FromMinutes(4)));
    }

    /// <summary>
    /// Verifies that a queued runtime starts from the preceding scenario's
    /// final output and cannot start on the call that completes that scenario.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWhenNextScenarioStartsUsesPreviousScenarioFinalOutput(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var first = CreateRuntime(DeviceId, metric, "First", TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var second = CreateRuntime(DeviceId, metric, "Second", TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var sequence = CreateSequence(DeviceId, metric, first, second);

        AssertNumericValue(sequence.Apply(CreateMeasurement(metric, baseline), TimeSpan.Zero), baseline + 3);
        sequence.Apply(CreateMeasurement(metric, baseline - 2), TimeSpan.FromSeconds(10));
        var completed = sequence.Apply(CreateMeasurement(metric, baseline + 2), TimeSpan.FromSeconds(20));

        AssertNumericValue(completed, baseline - 2);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(GetCurrentValue(second));
        Assert.Same(second, sequence.CurrentScenario);

        var nextScenarioResult = sequence.Apply(CreateMeasurement(metric, baseline + 2), TimeSpan.FromSeconds(25));

        AssertNumericValue(nextScenarioResult, baseline + 1);
        Assert.InRange(GetNumericValue(nextScenarioResult) - GetNumericValue(completed), 0, MaximumRise);
        Assert.Equal(ScenarioPhase.Active, second.Phase);
    }

    /// <summary>
    /// Verifies that argument failures do not replace the saved baseline
    /// before the first valid active measurement.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWithRejectedArgumentsPreservesPreviousOutput(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, baseline + 0.31), TimeSpan.Zero);
        var otherMetric = metric == SimulatorTelemetryMetric.Temperature
            ? SimulatorTelemetryMetric.Humidity
            : SimulatorTelemetryMetric.Temperature;

        var nullException = Assert.Throws<ArgumentNullException>(() => sequence.Apply(null!, TimeSpan.FromMinutes(1)));
        Assert.Equal("measurement", nullException.ParamName);
        var metricException = Assert.Throws<ArgumentException>(() => sequence.Apply(CreateMeasurement(otherMetric, baseline + 8), TimeSpan.FromMinutes(1)));
        Assert.Equal("measurement", metricException.ParamName);
        var elapsedException = Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Apply(CreateMeasurement(metric, baseline + 9), TimeSpan.FromTicks(-1)));
        Assert.Equal("elapsed", elapsedException.ParamName);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(GetCurrentValue(runtime));

        var result = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1));

        AssertNumericValue(result, baseline + 3.31);
    }

    /// <summary>
    /// Verifies that separate device sequences retain independent numeric
    /// baselines when their calls are interleaved.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWithIndependentSequencesKeepsBaselinesSeparate(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var firstRuntime = CreateRuntime(DeviceId, metric);
        var secondRuntime = CreateRuntime(OtherDeviceId, metric);
        var firstSequence = CreateSequence(DeviceId, metric, firstRuntime);
        var secondSequence = CreateSequence(OtherDeviceId, metric, secondRuntime);
        firstSequence.Apply(CreateMeasurement(metric, baseline + 0.1), TimeSpan.Zero);
        secondSequence.Apply(CreateMeasurement(metric, baseline + 1.5), TimeSpan.Zero);

        var first = firstSequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1));
        var second = secondSequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(1));

        AssertNumericValue(first, baseline + 3.1);
        AssertNumericValue(second, baseline + 4.5);
        Assert.Equal(ScenarioPhase.Active, firstRuntime.Phase);
        Assert.Equal(ScenarioPhase.Active, secondRuntime.Phase);
    }

    /// <summary>
    /// Verifies that baseline selection changes only the numeric result and
    /// preserves the current measurement's identity, metric, state, and time.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyAtScenarioStartPreservesCurrentMeasurementMetadata(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        var previousMeasurement = CreateMeasurement(metric, baseline + 0.31, InitialUtc);
        sequence.Apply(previousMeasurement, TimeSpan.Zero);
        var currentMeasurement = CreateMeasurement(metric, baseline + 0.77, InitialUtc.AddMinutes(1));

        var result = sequence.Apply(currentMeasurement, TimeSpan.FromMinutes(1));

        Assert.Equal(currentMeasurement.MeasurementId, result.MeasurementId);
        Assert.NotEqual(previousMeasurement.MeasurementId, result.MeasurementId);
        Assert.Equal(currentMeasurement.Metric, result.Metric);
        Assert.Equal(currentMeasurement.StateValue, result.StateValue);
        Assert.Equal(currentMeasurement.MeasuredAtUtc, result.MeasuredAtUtc);
        AssertNumericValue(result, baseline + 3.31);
        AssertNumericValue(currentMeasurement, baseline + 0.77);
        AssertNumericValue(previousMeasurement, baseline + 0.31);
    }

    /// <summary>
    /// Verifies that a late first active measurement uses the saved baseline
    /// while preserving a full active duration on the shifted scenario timeline.
    /// </summary>
    /// <param name="metric">The gradual numeric metric under test.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    public void ApplyWithDelayedFirstActiveMeasurementKeepsFullActiveDuration(SimulatorTelemetryMetric metric)
    {
        var baseline = GetNormalBaseline(metric);
        var runtime = CreateRuntime(DeviceId, metric);
        var sequence = CreateSequence(DeviceId, metric, runtime);
        sequence.Apply(CreateMeasurement(metric, baseline + 0.31), TimeSpan.Zero);

        var first = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromMinutes(10));
        AssertNumericValue(first, baseline + 3.31);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);

        var stillActive = sequence.Apply(CreateMeasurement(metric, baseline + 1), TimeSpan.FromSeconds(630));
        AssertNumericValue(stillActive, baseline + 6.31);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);

        var recovering = sequence.Apply(CreateMeasurement(metric, baseline - 1), TimeSpan.FromMinutes(11));
        AssertNumericValue(recovering, baseline + 6.31);
        Assert.Equal(ScenarioPhase.Recovering, runtime.Phase);
    }

    /// <summary>Returns a representative valid normal value for the metric.</summary>
    /// <param name="metric">The supported gradual numeric metric.</param>
    /// <returns>20 degrees Celsius or 80 percent relative humidity.</returns>
    private static double GetNormalBaseline(SimulatorTelemetryMetric metric) => metric switch
    {
        SimulatorTelemetryMetric.Temperature => 20,
        SimulatorTelemetryMetric.Humidity => 80,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unsupported test metric.")
    };

    /// <summary>Creates a fresh runtime with a fixed target and a rise limit of three.</summary>
    /// <param name="deviceId">The device that owns the runtime.</param>
    /// <param name="metric">The supported gradual numeric metric.</param>
    /// <param name="name">The scenario name, used to distinguish queued runtimes.</param>
    /// <param name="startsAfter">The start delay, defaulting to one minute.</param>
    /// <param name="duration">The active duration, defaulting to one minute.</param>
    /// <param name="recoveryDuration">The recovery duration, defaulting to one minute.</param>
    /// <returns>A fresh temperature or humidity runtime with no calculated value.</returns>
    /// <remarks>
    /// The recovery limit is intentionally large so recovery tests isolate
    /// target selection from the separate recovery rate limit.
    /// </remarks>
    private static IMeasurementScenarioRuntime CreateRuntime(
        Guid deviceId,
        SimulatorTelemetryMetric metric,
        string name = "GradualDeviation",
        TimeSpan? startsAfter = null,
        TimeSpan? duration = null,
        TimeSpan? recoveryDuration = null)
    {
        var start = startsAfter ?? TimeSpan.FromMinutes(1);
        var activeDuration = duration ?? TimeSpan.FromMinutes(1);
        var recovery = recoveryDuration ?? TimeSpan.FromMinutes(1);
        var target = new ScenarioTargetDefinition(ScenarioTargetMode.All);
        var random = new MidpointRandom();

        return metric switch
        {
            SimulatorTelemetryMetric.Temperature => new HighTemperatureScenarioRuntime(
                new HighTemperatureScenarioDefinition(name, start, activeDuration, target, true, recovery, 30, 34, MaximumRise, 100),
                deviceId, random),
            SimulatorTelemetryMetric.Humidity => new HighHumidityScenarioRuntime(
                new HighHumidityScenarioDefinition(name, start, activeDuration, target, true, recovery, 90, 94, MaximumRise, 100),
                deviceId, random),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unsupported test metric.")
        };
    }

    /// <summary>Creates a sequence from the supplied real runtime instances.</summary>
    /// <param name="deviceId">The device that owns every runtime.</param>
    /// <param name="metric">The common metric handled by every runtime.</param>
    /// <param name="runtimes">The independently observable runtime instances.</param>
    /// <returns>A new sequence with no preceding output.</returns>
    private static DeviceMetricScenarioSequence CreateSequence(
        Guid deviceId, SimulatorTelemetryMetric metric, params IMeasurementScenarioRuntime[] runtimes) =>
        new(deviceId, metric, runtimes);

    /// <summary>Creates an immutable numeric measurement with a new identifier.</summary>
    /// <param name="metric">The measurement metric.</param>
    /// <param name="numericValue">The valid numeric input value.</param>
    /// <param name="measuredAtUtc">The optional UTC time, defaulting to the fixed test origin.</param>
    /// <returns>A numeric measurement with no state value.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateMeasurement(
        SimulatorTelemetryMetric metric, double numericValue, DateTime? measuredAtUtc = null) =>
        new(Guid.NewGuid(), metric, numericValue, null, measuredAtUtc ?? InitialUtc);

    /// <summary>Reads the observable current value without advancing a runtime.</summary>
    /// <param name="runtime">The real gradual numeric runtime.</param>
    /// <returns>The current value, or null before the first calculation.</returns>
    private static double? GetCurrentValue(IMeasurementScenarioRuntime runtime) => runtime switch
    {
        HighTemperatureScenarioRuntime temperature => temperature.CurrentTemperature,
        HighHumidityScenarioRuntime humidity => humidity.CurrentHumidity,
        _ => throw new ArgumentException("Unsupported test runtime.", nameof(runtime))
    };

    /// <summary>Requires and returns the numeric output of a measurement.</summary>
    /// <param name="measurement">The numeric measurement to inspect.</param>
    /// <returns>The non-null numeric value.</returns>
    private static double GetNumericValue(SimulatorTelemetryMeasurementRequest measurement)
    {
        Assert.True(measurement.NumericValue.HasValue);
        return measurement.NumericValue.GetValueOrDefault();
    }

    /// <summary>Compares numeric output with tolerance for floating-point arithmetic.</summary>
    /// <param name="measurement">The actual numeric measurement.</param>
    /// <param name="expected">The expected numeric value.</param>
    private static void AssertNumericValue(SimulatorTelemetryMeasurementRequest measurement, double expected) =>
        Assert.Equal(expected, GetNumericValue(measurement), precision: 10);

    /// <summary>Fixes target selection at the midpoint of the configured range.</summary>
    private sealed class MidpointRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
    }
}
