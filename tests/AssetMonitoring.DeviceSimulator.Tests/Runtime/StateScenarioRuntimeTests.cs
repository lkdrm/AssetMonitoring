using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using static AssetMonitoring.DeviceSimulator.Tests.Runtime.StateScenarioTestData;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>Verifies the shared observable contract of door-open and light-on runtimes.</summary>
public sealed class StateScenarioRuntimeTests
{
    private const SimulatorTelemetryMetric Door = SimulatorTelemetryMetric.DoorState;
    private const SimulatorTelemetryMetric Light = SimulatorTelemetryMetric.LightState;

    /// <summary>Verifies initial identity, definition ownership, phase, and absent state.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ConstructorPreservesIdentityAndStartsPendingWithoutCurrentState(SimulatorTelemetryMetric metric)
    {
        var definition = CreateDefinition(metric);
        var runtime = CreateRuntime(definition);

        Assert.Equal(DeviceId, runtime.DeviceId);
        Assert.Equal(metric, runtime.Metric);
        Assert.Same(definition, runtime.Definition);
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
        if (runtime is DoorOpenScenarioRuntime door)
            Assert.Same(definition, door.Definition);
        else
            Assert.Same(definition, Assert.IsType<LightOnScenarioRuntime>(runtime).Definition);
    }

    /// <summary>Verifies the required configuration dependency.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ConstructorWithNullDefinitionThrows(SimulatorTelemetryMetric metric)
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            if (metric == Door)
                _ = new DoorOpenScenarioRuntime(null!, DeviceId);
            else
                _ = new LightOnScenarioRuntime(null!, DeviceId);
        });
        Assert.Equal("definition", error.ParamName);
    }

    /// <summary>Verifies that a runtime requires a nonempty owning device identifier.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ConstructorWithEmptyDeviceIdThrows(SimulatorTelemetryMetric metric)
    {
        var error = Assert.Throws<ArgumentException>(() => CreateRuntime(CreateDefinition(metric), Guid.Empty));
        Assert.Equal("deviceId", error.ParamName);
    }

    /// <summary>Verifies that a null measurement is rejected before execution state changes.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithNullMeasurementThrowsWithoutChangingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var error = Assert.Throws<ArgumentNullException>(() => runtime.Apply(null!, Start));
        Assert.Equal("measurement", error.ParamName);
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies isolation between the two boolean telemetry metrics.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithOtherStateMetricThrowsWithoutChangingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var measurement = CreateMeasurement(metric == Door ? Light : Door);
        var error = Assert.Throws<ArgumentException>(() => runtime.Apply(measurement, Start));
        Assert.Equal("measurement", error.ParamName);
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies metric validation takes precedence over numeric payload shape.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithNumericMetricReportsMetricErrorBeforeMissingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var measurement = CreateMeasurement(SimulatorTelemetryMetric.Temperature, null, 22);
        var error = Assert.Throws<ArgumentException>(() => runtime.Apply(measurement, Start));
        Assert.Equal("measurement", error.ParamName);
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies the nonnegative elapsed-time requirement.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithNegativeElapsedThrowsWithoutChangingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var elapsed = TimeSpan.FromTicks(-1);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.Apply(CreateMeasurement(metric), elapsed));
        Assert.Equal("elapsed", error.ParamName);
        Assert.Equal(elapsed, error.ActualValue);
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies that a state measurement requires a boolean payload.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithMissingStateThrowsWithoutChangingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var error = Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(CreateMeasurement(metric, null), Start));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies that any numeric payload is invalid for a state metric.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="numeric">The unwanted numeric payload.</param>
    [Theory]
    [InlineData(Door, 0d)]
    [InlineData(Door, double.NaN)]
    [InlineData(Light, 0d)]
    [InlineData(Light, double.NaN)]
    public void ApplyWithNumericPayloadThrowsWithoutChangingState(SimulatorTelemetryMetric metric, double numeric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var error = Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(CreateMeasurement(metric, false, numeric), Start));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies the explicitly unsupported manual-recovery configuration.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithoutAutomaticRecoveryThrowsWithoutChangingState(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric, autoRecover: false));
        var error = Assert.Throws<NotSupportedException>(() => runtime.Apply(CreateMeasurement(metric), Start));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        AssertUnstarted(runtime);
    }

    /// <summary>Verifies that both normal boolean values remain unchanged before activation.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="normalState">The normal incoming state.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public void ApplyBeforeStartPreservesOriginalMeasurementAndNormalState(
        SimulatorTelemetryMetric metric, bool normalState)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var input = CreateMeasurement(metric, normalState);

        var result = runtime.Apply(input, Start - TimeSpan.FromTicks(1));

        Assert.Same(input, result);
        Assert.Equal(normalState, CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
    }

    /// <summary>Verifies exact activation, forced true output, unchanged metadata, and immutable input.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="normalState">The normal incoming state.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public void ApplyAtStartForcesTrueAndPreservesMeasurementMetadata(
        SimulatorTelemetryMetric metric, bool normalState)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var input = CreateMeasurement(metric, normalState);

        var result = runtime.Apply(input, Start);

        Assert.NotSame(input, result);
        Assert.Equal(input with { StateValue = true }, result);
        Assert.Equal(normalState, input.StateValue);
        Assert.True(result.StateValue);
        Assert.Null(result.NumericValue);
        Assert.Equal(DateTimeKind.Utc, result.MeasuredAtUtc.Kind);
        Assert.True(CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Verifies that the final tick before the active end remains active.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyImmediatelyBeforeEndRemainsActive(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));

        var result = runtime.Apply(CreateMeasurement(metric), Start + Duration - TimeSpan.FromTicks(1));

        Assert.True(result.StateValue);
        Assert.True(CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Verifies completion exactly at StartsAfter plus Duration without a recovery phase.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="normalState">The fresh normal state at completion.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public void ApplyAtEndCompletesAndPreservesFreshNormalMeasurement(
        SimulatorTelemetryMetric metric, bool normalState)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        runtime.Apply(CreateMeasurement(metric), Start);
        var normal = CreateMeasurement(metric, normalState);

        var result = runtime.Apply(normal, Start + Duration);

        Assert.Same(normal, result);
        Assert.Equal(normalState, CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
    }

    /// <summary>Verifies a full cycle and forwarding of fresh normal states after completion.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyThroughFullCycleRestoresCurrentNormalValuesAfterCompletion(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var pending = CreateMeasurement(metric, false);
        Assert.Same(pending, runtime.Apply(pending, TimeSpan.Zero));
        Assert.False(CurrentState(runtime));
        Assert.True(runtime.Apply(CreateMeasurement(metric), Start).StateValue);
        Assert.True(runtime.Apply(CreateMeasurement(metric), Start + Duration - TimeSpan.FromTicks(1)).StateValue);
        var completion = CreateMeasurement(metric, true);
        Assert.Same(completion, runtime.Apply(completion, Start + Duration));
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
        Assert.True(CurrentState(runtime));
        var next = CreateMeasurement(metric, false);
        Assert.Same(next, runtime.Apply(next, Start + Duration + TimeSpan.FromSeconds(1)));
        Assert.False(CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
    }

    /// <summary>Verifies immediate activation when the configured start delay is zero.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithZeroStartDelayActivatesOnFirstMeasurement(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric, startsAfter: TimeSpan.Zero));
        Assert.True(runtime.Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        Assert.Equal(ScenarioPhase.Active, runtime.Phase);
    }

    /// <summary>Verifies direct late calls use the runtime timeline supplied by the caller.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyFirstCalledAfterEndReturnsNormalAndCompletes(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var input = CreateMeasurement(metric, false);
        Assert.Same(input, runtime.Apply(input, TimeSpan.FromHours(1)));
        Assert.False(CurrentState(runtime));
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
    }

    /// <summary>Verifies rejected input cannot change previously observable runtime state.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="completed">Whether to complete the runtime before invalid input.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public void ApplyWithInvalidInputPreservesPreviouslyReachedState(
        SimulatorTelemetryMetric metric, bool completed)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        runtime.Apply(CreateMeasurement(metric), Start);
        if (completed)
            runtime.Apply(CreateMeasurement(metric), Start + Duration);
        var phase = runtime.Phase;
        var state = CurrentState(runtime);

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(
            CreateMeasurement(metric, null), Start + Duration + TimeSpan.FromSeconds(1)));

        Assert.Equal(phase, runtime.Phase);
        Assert.Equal(state, CurrentState(runtime));
    }

    /// <summary>Verifies a shared definition does not share state between different devices.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void RuntimesForDifferentDevicesHaveIndependentExecutionState(SimulatorTelemetryMetric metric)
    {
        var definition = CreateDefinition(metric);
        var first = CreateRuntime(definition, CreateDevice(1).Id);
        var second = CreateRuntime(definition, CreateDevice(2).Id);

        first.Apply(CreateMeasurement(metric), Start + Duration);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        AssertUnstarted(second);
        Assert.True(second.Apply(CreateMeasurement(metric), Start).StateValue);
        Assert.Equal(ScenarioPhase.Active, second.Phase);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        Assert.False(CurrentState(first));
    }

    /// <summary>Verifies repeated runtime creation for the same device starts fresh.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void FreshRuntimeForSameDeviceDoesNotReuseCompletedState(SimulatorTelemetryMetric metric)
    {
        var definition = CreateDefinition(metric);
        var first = CreateRuntime(definition);
        first.Apply(CreateMeasurement(metric), Start + Duration);
        var second = CreateRuntime(definition);

        Assert.NotSame(first, second);
        AssertUnstarted(second);
        Assert.True(second.Apply(CreateMeasurement(metric), Start).StateValue);
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
    }

    /// <summary>Verifies an untouched runtime still has its initial observable state.</summary>
    /// <param name="runtime">The runtime to inspect.</param>
    private static void AssertUnstarted(IMeasurementScenarioRuntime runtime)
    {
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
    }
}
