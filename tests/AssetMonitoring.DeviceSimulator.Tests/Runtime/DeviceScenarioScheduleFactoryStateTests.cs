using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using static AssetMonitoring.DeviceSimulator.Tests.Runtime.StateScenarioTestData;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>Verifies state-runtime preparation and its interaction with numeric scenarios.</summary>
public sealed class DeviceScenarioScheduleFactoryStateTests
{
    private const SimulatorTelemetryMetric Door = SimulatorTelemetryMetric.DoorState;
    private const SimulatorTelemetryMetric Light = SimulatorTelemetryMetric.LightState;

    /// <summary>Verifies each new supported definition creates a functioning schedule.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateStateScenarioProducesActiveAndCompletedMeasurements(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice();
        var definition = CreateDefinition(metric, startsAfter: TimeSpan.Zero);
        var result = new DeviceScenarioScheduleFactory().Create(
            new[] { new ResolvedScenario(definition, new[] { device }) }, new Random(42));

        var schedule = Assert.Single(result).Value;
        Assert.Equal(device.Id, schedule.DeviceId);
        Assert.True(schedule.Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        var normal = CreateMeasurement(metric, false);
        Assert.Same(normal, schedule.Apply(normal, Duration));
        var next = CreateMeasurement(metric, true);
        Assert.Same(next, schedule.Apply(next, Duration + TimeSpan.FromSeconds(1)));
    }

    /// <summary>Verifies supported state definitions without targets produce an empty result.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateSupportedStateScenarioWithoutDevicesReturnsEmpty(SimulatorTelemetryMetric metric)
    {
        var result = new DeviceScenarioScheduleFactory().Create(
            new[] { new ResolvedScenario(CreateDefinition(metric), Array.Empty<SimulatorDeviceResponse>()) },
            new Random(42));
        Assert.Empty(result);
    }

    /// <summary>Verifies state-runtime device guards propagate through preparation.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateStateScenarioWithEmptyDeviceIdThrows(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice() with { Id = Guid.Empty };
        var error = Assert.Throws<ArgumentException>(() => new DeviceScenarioScheduleFactory().Create(
            new[] { new ResolvedScenario(CreateDefinition(metric), new[] { device }) }, new Random(42)));
        Assert.Equal("deviceId", error.ParamName);
    }

    /// <summary>Verifies completion on one device does not complete another device's runtime.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateStateScenarioUsesIndependentRuntimeForEveryDevice(SimulatorTelemetryMetric metric)
    {
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var definition = CreateDefinition(metric, startsAfter: TimeSpan.Zero);
        var schedules = new DeviceScenarioScheduleFactory().Create(
            new[] { new ResolvedScenario(definition, new[] { first, second }) }, new Random(42));

        Assert.Equal(2, schedules.Count);
        Assert.True(schedules[first.Id].Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        Assert.False(schedules[first.Id].Apply(CreateMeasurement(metric), Duration).StateValue);
        Assert.True(schedules[second.Id].Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        Assert.False(schedules[first.Id].Apply(CreateMeasurement(metric), Duration).StateValue);
    }

    /// <summary>Verifies separate factory calls create fresh state for the same inputs.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateRepeatedlyDoesNotReuseCompletedState(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice();
        var inputs = new[] { new ResolvedScenario(
            CreateDefinition(metric, startsAfter: TimeSpan.Zero), new[] { device }) };
        var factory = new DeviceScenarioScheduleFactory();
        var first = factory.Create(inputs, new Random(42))[device.Id];
        first.Apply(CreateMeasurement(metric), TimeSpan.Zero);
        first.Apply(CreateMeasurement(metric), Duration);

        var second = factory.Create(inputs, new Random(42))[device.Id];

        Assert.NotSame(first, second);
        Assert.True(second.Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
    }

    /// <summary>Verifies state-runtime creation and execution preserve the supplied random state.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateStateScenariosDoesNotConsumeRandomValues(SimulatorTelemetryMetric metric)
    {
        var devices = new[] { CreateDevice(1), CreateDevice(2) };
        var random = new Random(-17);
        var expected = new Random(-17);
        var schedules = new DeviceScenarioScheduleFactory().Create(new[]
        {
            new ResolvedScenario(CreateDefinition(metric, startsAfter: TimeSpan.Zero), devices)
        }, random);
        foreach (var device in devices)
        {
            schedules[device.Id].Apply(CreateMeasurement(metric), TimeSpan.Zero);
            schedules[device.Id].Apply(CreateMeasurement(metric), Duration);
        }
        for (var index = 0; index < 8; index++)
            Assert.Equal(expected.Next(), random.Next());
    }

    /// <summary>Verifies state schedules preserve measurements belonging to an absent metric.</summary>
    /// <param name="metric">The configured state metric.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateStateSchedulePreservesUnconfiguredStateMetric(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice();
        var schedule = new DeviceScenarioScheduleFactory().Create(new[]
        {
            new ResolvedScenario(CreateDefinition(metric), new[] { device })
        }, new Random(42))[device.Id];
        var other = CreateMeasurement(metric == Door ? Light : Door);
        Assert.Same(other, schedule.Apply(other, TimeSpan.FromHours(1)));
        Assert.True(schedule.Apply(CreateMeasurement(metric), TimeSpan.FromHours(1)).StateValue);
    }

    /// <summary>Verifies state definitions do not displace numeric random draws in mixed plans.</summary>
    [Fact]
    public void CreateMixedStateAndNumericScenariosPreservesNumericTargetSelection()
    {
        var device = CreateDevice(capabilities: new[]
        {
            SimulatorDeviceCapability.DoorState, SimulatorDeviceCapability.LightState,
            SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity
        });
        var temperature = new HighTemperatureScenarioDefinition("Temperature", TimeSpan.Zero,
            TimeSpan.FromMinutes(5), new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true, TimeSpan.FromMinutes(1), 40, 50, 100, 100);
        var humidity = new HighHumidityScenarioDefinition("Humidity", TimeSpan.Zero,
            TimeSpan.FromMinutes(5), new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true, TimeSpan.FromMinutes(1), 90, 95, 100, 100);
        var definitions = new ScenarioDefinition[]
        {
            CreateDefinition(Door, startsAfter: TimeSpan.Zero), temperature,
            CreateDefinition(Light, startsAfter: TimeSpan.Zero), humidity
        };
        var random = new Random(42);
        var expected = new Random(42);
        var expectedTemperature = 40 + expected.NextDouble() * 10;
        var expectedHumidity = 90 + expected.NextDouble() * 5;
        var schedule = new DeviceScenarioScheduleFactory().Create(definitions
            .Select(definition => new ResolvedScenario(definition, new[] { device })).ToArray(), random)[device.Id];

        Assert.True(schedule.Apply(CreateMeasurement(Door), TimeSpan.Zero).StateValue);
        Assert.True(schedule.Apply(CreateMeasurement(Light), TimeSpan.Zero).StateValue);
        Assert.Equal(expectedTemperature, schedule.Apply(CreateMeasurement(
            SimulatorTelemetryMetric.Temperature, null, 20), TimeSpan.Zero).NumericValue);
        Assert.Equal(expectedHumidity, schedule.Apply(CreateMeasurement(
            SimulatorTelemetryMetric.Humidity, null, 80), TimeSpan.Zero).NumericValue);
        Assert.Equal(expected.Next(), random.Next());
    }

    /// <summary>Verifies factory grouping retains all queued runtimes and orders their start delays.</summary>
    /// <param name="metric">The state scenario under test.</param>
    [Theory]
    [InlineData(Door)]
    [InlineData(Light)]
    public void CreateMultipleStateScenariosOrdersAndQueuesThem(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice();
        var early = CreateDefinition(metric, "Early", TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var late = CreateDefinition(metric, "Late", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var schedule = new DeviceScenarioScheduleFactory().Create(new[]
        {
            new ResolvedScenario(late, new[] { device }),
            new ResolvedScenario(early, new[] { device })
        }, new Random(42))[device.Id];

        Assert.True(schedule.Apply(CreateMeasurement(metric), TimeSpan.Zero).StateValue);
        Assert.False(schedule.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(1)).StateValue);
        Assert.True(schedule.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(1)).StateValue);
        Assert.False(schedule.Apply(CreateMeasurement(metric), TimeSpan.FromMinutes(2)).StateValue);
    }
}
