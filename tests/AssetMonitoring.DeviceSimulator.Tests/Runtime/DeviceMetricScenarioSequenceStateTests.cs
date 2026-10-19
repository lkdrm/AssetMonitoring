using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Runtime;
using static AssetMonitoring.DeviceSimulator.Tests.Runtime.StateScenarioTestData;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>Verifies queued state scenarios and independent per-metric scheduling.</summary>
public sealed class DeviceMetricScenarioSequenceStateTests
{
    private const SimulatorTelemetryMetric Door = SimulatorTelemetryMetric.DoorState;
    private const SimulatorTelemetryMetric Light = SimulatorTelemetryMetric.LightState;

    /// <summary>Verifies pending sequences preserve both normal values without invoking the runtime.</summary>
    /// <param name="metric">The state scenario under test.</param>
    /// <param name="normalState">The normal incoming state.</param>
    [Theory]
    [InlineData(Door, false)]
    [InlineData(Door, true)]
    [InlineData(Light, false)]
    [InlineData(Light, true)]
    public void ApplyBeforeStartReturnsOriginalAndLeavesRuntimeUnstarted(
        SimulatorTelemetryMetric metric, bool normalState)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var sequence = new DeviceMetricScenarioSequence(DeviceId, metric, new[] { runtime });
        var normal = CreateMeasurement(metric, normalState);
        Assert.Same(normal, sequence.Apply(normal, TimeSpan.Zero));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
    }

    /// <summary>Verifies a late first measurement receives the full active duration.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyWithLateFirstMeasurementKeepsFullActiveDuration(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var sequence = new DeviceMetricScenarioSequence(DeviceId, metric, new[] { runtime });
        var actualStart = TimeSpan.FromMinutes(10);
        Assert.True(sequence.Apply(CreateMeasurement(metric), actualStart).StateValue);
        Assert.True(sequence.Apply(CreateMeasurement(metric),
            actualStart + Duration - TimeSpan.FromTicks(1)).StateValue);
        var normal = CreateMeasurement(metric);
        Assert.Same(normal, sequence.Apply(normal, actualStart + Duration));
        Assert.Equal(ScenarioPhase.Completed, runtime.Phase);
        Assert.Null(sequence.CurrentScenario);
    }

    /// <summary>Verifies equal-delay input order, one scenario per call, and full queued duration.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyQueuedStateScenariosWaitsForNextCallAndPreservesStableOrder(SimulatorTelemetryMetric metric)
    {
        var first = CreateRuntime(CreateDefinition(metric, "Zeta", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        var second = CreateRuntime(CreateDefinition(metric, "Alpha", TimeSpan.Zero, TimeSpan.FromMinutes(2)));
        var sequence = new DeviceMetricScenarioSequence(DeviceId, metric, new[] { first, second });
        Assert.Same(first, sequence.CurrentScenario);
        Assert.True(sequence.Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        Assert.Null(CurrentState(second));

        var normal = CreateMeasurement(metric);
        Assert.Same(normal, sequence.Apply(normal, TimeSpan.FromMinutes(1)));
        Assert.Equal(ScenarioPhase.Completed, first.Phase);
        Assert.Same(second, sequence.CurrentScenario);
        Assert.Equal(ScenarioPhase.Pending, second.Phase);
        Assert.Null(CurrentState(second));

        Assert.True(sequence.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(1)).StateValue);
        Assert.True(sequence.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(2)).StateValue);
        normal = CreateMeasurement(metric);
        Assert.Same(normal, sequence.Apply(normal, TimeSpan.FromMinutes(3)));
        Assert.Equal(ScenarioPhase.Completed, second.Phase);
        Assert.Null(sequence.CurrentScenario);

        normal = CreateMeasurement(metric, true);
        Assert.Same(normal, sequence.Apply(normal, TimeSpan.FromMinutes(4)));
        Assert.False(CurrentState(first));
        Assert.False(CurrentState(second));
    }

    /// <summary>Verifies normal behavior during a gap before a later queued scenario.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ApplyBetweenQueuedScenariosPreservesNormalStateUntilNextStart(SimulatorTelemetryMetric metric)
    {
        var first = CreateRuntime(CreateDefinition(metric, "First", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        var second = CreateRuntime(CreateDefinition(metric, "Second", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        var sequence = new DeviceMetricScenarioSequence(DeviceId, metric, new[] { first, second });
        sequence.Apply(CreateMeasurement(metric), TimeSpan.Zero);
        sequence.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(1));
        var normal = CreateMeasurement(metric, true);
        Assert.Same(normal, sequence.Apply(normal, TimeSpan.FromMinutes(3)));
        Assert.Null(CurrentState(second));
        Assert.True(sequence.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(5)).StateValue);
        Assert.False(sequence.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(6)).StateValue);
        Assert.Null(sequence.CurrentScenario);
    }

    /// <summary>Verifies an unrelated metric cannot start or advance a state runtime.</summary>
    /// <param name="metric">The state metric configured in the schedule.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void ScheduleWithOtherStateMetricLeavesConfiguredRuntimeUntouched(SimulatorTelemetryMetric metric)
    {
        var runtime = CreateRuntime(CreateDefinition(metric));
        var schedule = CreateSchedule(DeviceId, runtime);
        var other = CreateMeasurement(metric == Door ? Light : Door, true);
        Assert.Same(other, schedule.Apply(other, TimeSpan.FromHours(1)));
        Assert.Equal(ScenarioPhase.Pending, runtime.Phase);
        Assert.Null(CurrentState(runtime));
    }

    /// <summary>Verifies door and light schedules retain independent timing in either call order.</summary>
    /// <param name="lightFirst">Whether each pair of measurements processes light before doors.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScheduleRunsDoorAndLightIndependentlyInEitherMeasurementOrder(bool lightFirst)
    {
        var door = CreateRuntime(CreateDefinition(Door, duration: TimeSpan.FromMinutes(1)));
        var light = CreateRuntime(CreateDefinition(Light,
            startsAfter: TimeSpan.FromMinutes(2), duration: TimeSpan.FromMinutes(3)));
        var schedule = CreateSchedule(DeviceId, door, light);
        var metrics = lightFirst ? new[] { Light, Door } : new[] { Door, Light };

        foreach (var metric in metrics)
        {
            var output = schedule.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(1));
            Assert.Equal(metric == Door, output.StateValue);
        }
        Assert.Equal(ScenarioPhase.Active, door.Phase);
        Assert.Equal(ScenarioPhase.Pending, light.Phase);
        Assert.Null(CurrentState(light));

        foreach (var metric in metrics)
        {
            var output = schedule.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(2));
            Assert.Equal(metric == Light, output.StateValue);
        }
        Assert.Equal(ScenarioPhase.Completed, door.Phase);
        Assert.Equal(ScenarioPhase.Active, light.Phase);
        var normalDoor = CreateMeasurement(Door, true);
        Assert.Same(normalDoor, schedule.Apply(normalDoor, TimeSpan.FromMinutes(3)));
        Assert.False(CurrentState(door));
        Assert.True(schedule.Apply(CreateMeasurement(Light), TimeSpan.FromMinutes(3)).StateValue);
        var temperature = CreateMeasurement(SimulatorTelemetryMetric.Temperature, null, 22.25);
        Assert.Same(temperature, schedule.Apply(temperature, TimeSpan.FromMinutes(3)));
        Assert.False(schedule.Apply(CreateMeasurement(Light), TimeSpan.FromMinutes(5)).StateValue);
        Assert.Equal(ScenarioPhase.Completed, light.Phase);
    }
}
