using System;
using System.Collections.Generic;
using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Xunit;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies factory guards, device grouping, independent execution state,
/// scenario ordering, and repeatable preparation with a supplied random source.
/// </summary>
/// <remarks>
/// Tests use real runtimes, metric sequences, and schedules through their public APIs.
/// Each temperature helper call applies a fresh measurement. No reflection,
/// HTTP requests, timers, real delays, or additional packages are required.
/// Inputs represent validated definitions and already resolved target devices.
/// </remarks>
public sealed class DeviceScenarioScheduleFactoryTests
{
    /// <summary>Verifies that the resolved scenario collection is required.</summary>
    [Fact]
    public void CreateWithNullResolvedScenariosThrowsArgumentNullException()
    {
        var factory = new DeviceScenarioScheduleFactory();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            factory.Create(null!, new Random(42)));

        Assert.Equal("resolvedScenarios", exception.ParamName);
    }

    /// <summary>Verifies that a random source is required even for an empty plan.</summary>
    [Fact]
    public void CreateWithNullRandomThrowsArgumentNullException()
    {
        var factory = new DeviceScenarioScheduleFactory();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            factory.Create(Array.Empty<ResolvedScenario>(), null!));

        Assert.Equal("random", exception.ParamName);
    }

    /// <summary>
    /// Verifies that normal operation without scenarios produces no sequences
    /// and does not consume random values.
    /// </summary>
    [Fact]
    public void CreateWithEmptyPlanReturnsEmptyDictionaryWithoutUsingRandom()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var random = new ScriptedRandom();

        var result = factory.Create(Array.Empty<ResolvedScenario>(), random);

        Assert.Empty(result);
        Assert.Equal(0, random.CallCount);
    }

    /// <summary>Verifies that an empty resolved target list creates no runtime or sequence.</summary>
    [Fact]
    public void CreateWithNoResolvedDevicesReturnsEmptyDictionaryWithoutUsingRandom()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var random = new ScriptedRandom();
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition(), Array.Empty<SimulatorDeviceResponse>())
        };

        var result = factory.Create(resolved, random);

        Assert.Empty(result);
        Assert.Equal(0, random.CallCount);
    }

    /// <summary>
    /// Verifies that a resolved device receives a working scenario whose target
    /// is selected during preparation and retained for subsequent measurements.
    /// </summary>
    [Fact]
    public void CreateWithOneDeviceReturnsWorkingSequenceWithRetainedTarget()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var random = new ScriptedRandom(0.5);
        var resolved = new[] { new ResolvedScenario(CreateDefinition(), new[] { device }) };

        var result = factory.Create(resolved, random);

        var entry = Assert.Single(result);
        Assert.Equal(device.Id, entry.Key);
        Assert.Equal(1, random.CallCount);
        Assert.Equal(32.0, ReadTemperature(entry.Value, 22, TimeSpan.Zero));
        Assert.Equal(32.0, ReadTemperature(entry.Value, 24, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>
    /// Verifies that every selected device receives a sequence keyed by its
    /// identifier, including devices that happen to share a catalog code.
    /// </summary>
    [Fact]
    public void CreateGroupsDistinctDevicesByIdInsteadOfCode()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var first = CreateDevice(1);
        var second = CreateDevice(2) with { Code = first.Code };
        var third = CreateDevice(3);
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition(), new[] { third, first, second })
        };

        var result = factory.Create(resolved, new FixedRandom());

        Assert.Equal(3, result.Count);
        foreach (var device in new[] { first, second, third })
        {
            Assert.True(result.ContainsKey(device.Id));
            Assert.Equal(32.0, ReadTemperature(result[device.Id], 22, TimeSpan.Zero));
        }
    }

    /// <summary>
    /// Verifies that advancing one device cannot change another device's
    /// temperature, even when both use the same definition object.
    /// </summary>
    [Fact]
    public void CreateKeepsTemperatureStateIndependentBetweenDevices()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 2 };
        var result = factory.Create(
            new[] { new ResolvedScenario(definition, new[] { first, second }) },
            new FixedRandom());

        Assert.NotSame(result[first.Id], result[second.Id]);
        Assert.Equal(22.0, ReadTemperature(result[first.Id], 20, TimeSpan.Zero));
        Assert.Equal(26.0, ReadTemperature(result[second.Id], 24, TimeSpan.Zero));
        Assert.Equal(24.0, ReadTemperature(result[first.Id], 20, TimeSpan.FromSeconds(30)));
        Assert.Equal(28.0, ReadTemperature(result[second.Id], 24, TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Verifies that completing one device's queue cannot complete or advance
    /// another device's queue before that device has produced any measurements.
    /// </summary>
    [Fact]
    public void CreateKeepsScenarioCompletionIndependentBetweenDevices()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var result = factory.Create(
            new[] { new ResolvedScenario(CreateDefinition(), new[] { first, second }) },
            new FixedRandom());

        AssertFirstScenarioCompletes(result[first.Id], 32);

        Assert.Equal(23.0, ReadTemperature(result[first.Id], 23, TimeSpan.FromSeconds(121)));
        // The second device starts late and must still execute its own scenario.
        Assert.Equal(32.0, ReadTemperature(result[second.Id], 22, TimeSpan.FromSeconds(121)));
        Assert.Equal(32.0, ReadTemperature(result[second.Id], 22, TimeSpan.FromSeconds(151)));
    }

    /// <summary>
    /// Verifies that distinct response objects with the same device identifier
    /// contribute scenarios to one queue rather than separate queues.
    /// </summary>
    [Fact]
    public void CreateGroupsMultipleScenariosForTheSameDeviceIdIntoOneSequence()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var otherResponse = device with { Name = "Updated device name" };
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition("First"), new[] { device }),
            new ResolvedScenario(CreateDefinition("Second", 40), new[] { otherResponse })
        };

        var result = factory.Create(resolved, new FixedRandom());

        var entry = Assert.Single(result);
        Assert.Equal(device.Id, entry.Key);
        AssertFirstScenarioCompletes(entry.Value, 32);
        // The next scenario begins on a new measurement, even at the same time.
        Assert.Equal(42.0, ReadTemperature(entry.Value, 22, TimeSpan.FromSeconds(120)));
        Assert.Equal(42.0, ReadTemperature(entry.Value, 22, TimeSpan.FromSeconds(180)));
        Assert.Equal(22.0, ReadTemperature(entry.Value, 22, TimeSpan.FromSeconds(240)));
        Assert.Equal(24.0, ReadTemperature(entry.Value, 24, TimeSpan.FromSeconds(241)));
    }

    /// <summary>
    /// Verifies that overlapping target sets do not attach a scenario to
    /// devices selected only by another scenario.
    /// </summary>
    [Fact]
    public void CreateAssignsOnlyResolvedScenariosToEachDevice()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var first = CreateDevice(1);
        var shared = CreateDevice(2);
        var last = CreateDevice(3);
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition("First"), new[] { first, shared }),
            new ResolvedScenario(CreateDefinition("Second", 40), new[] { shared, last })
        };

        var result = factory.Create(resolved, new FixedRandom());

        Assert.Equal(3, result.Count);
        AssertFirstScenarioCompletes(result[first.Id], 32);
        AssertFirstScenarioCompletes(result[shared.Id], 32);
        AssertFirstScenarioCompletes(result[last.Id], 42);
        Assert.Equal(24.0, ReadTemperature(result[first.Id], 24, TimeSpan.FromSeconds(121)));
        Assert.Equal(42.0, ReadTemperature(result[shared.Id], 22, TimeSpan.FromSeconds(121)));
        Assert.Equal(24.0, ReadTemperature(result[last.Id], 24, TimeSpan.FromSeconds(121)));
    }

    /// <summary>
    /// Verifies that equal planned starts preserve the resolved plan order,
    /// including an order that differs from alphabetical scenario names.
    /// </summary>
    /// <param name="reverse">Whether to place the hotter scenario first.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatePreservesPlanOrderForEqualScenarioStartTimes(bool reverse)
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var cooler = new ResolvedScenario(CreateDefinition("Zeta"), new[] { device });
        var hotter = new ResolvedScenario(CreateDefinition("Alpha", 40), new[] { device });
        var resolved = reverse ? new[] { hotter, cooler } : new[] { cooler, hotter };

        var result = factory.Create(resolved, new FixedRandom());

        AssertFirstScenarioCompletes(result[device.Id], reverse ? 42 : 32);
        Assert.Equal(reverse ? 32.0 : 42.0,
            ReadTemperature(result[device.Id], 22, TimeSpan.FromSeconds(120)));
    }

    /// <summary>
    /// Verifies that the factory returns chronologically ordered sequences
    /// when resolved scenarios arrive in a different order.
    /// </summary>
    [Fact]
    public void CreateReturnsSequenceOrderedByPlannedStartTime()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var later = CreateDefinition("Later", 40) with { StartsAfter = TimeSpan.FromSeconds(120) };
        var earlier = CreateDefinition("Earlier") with { StartsAfter = TimeSpan.FromSeconds(60) };
        var resolved = new[]
        {
            new ResolvedScenario(later, new[] { device }),
            new ResolvedScenario(earlier, new[] { device })
        };

        var result = factory.Create(resolved, new FixedRandom());
        var schedule = result[device.Id];

        Assert.Equal(23.0, ReadTemperature(schedule, 23, TimeSpan.FromSeconds(59)));
        Assert.Equal(32.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(60)));
        Assert.Equal(32.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(120)));
        Assert.Equal(22.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(180)));
        Assert.Equal(42.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(181)));
        Assert.Equal(42.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(240)));
        Assert.Same(later, resolved[0].Definition);
        Assert.Same(earlier, resolved[1].Definition);
    }

    /// <summary>
    /// Verifies that each factory call creates fresh state, even when the same
    /// factory, resolved collection, definition, and random source are reused.
    /// </summary>
    [Fact]
    public void CreateOnRepeatedCallsReturnsIndependentExecutionState()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var definition = CreateDefinition() with { MaximumRisePerMeasurement = 2 };
        var resolved = new[] { new ResolvedScenario(definition, new[] { device }) };
        var random = new FixedRandom();
        var first = factory.Create(resolved, random);

        Assert.Equal(22.0, ReadTemperature(first[device.Id], 20, TimeSpan.Zero));
        Assert.Equal(24.0, ReadTemperature(first[device.Id], 20, TimeSpan.FromSeconds(30)));

        var second = factory.Create(resolved, random);

        Assert.NotSame(first, second);
        Assert.NotSame(first[device.Id], second[device.Id]);
        Assert.Equal(22.0, ReadTemperature(second[device.Id], 20, TimeSpan.Zero));
        Assert.Equal(26.0, ReadTemperature(first[device.Id], 20, TimeSpan.FromSeconds(40)));
    }

    /// <summary>Verifies that later preparations do not retain devices from an earlier plan.</summary>
    [Fact]
    public void CreateAfterNonemptyPlanDoesNotRetainPreviousDevices()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var first = factory.Create(
            new[] { new ResolvedScenario(CreateDefinition(), new[] { device }) },
            new FixedRandom());

        var second = factory.Create(Array.Empty<ResolvedScenario>(), new FixedRandom());

        Assert.Empty(second);
        Assert.Single(first);
        Assert.Equal(32.0, ReadTemperature(first[device.Id], 22, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies repeatability for identical inputs and fresh random sources
    /// with the same seed, without hard-coding the framework's random values.
    /// </summary>
    /// <param name="seed">The shared seed for the independent preparations.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-17)]
    public void CreateWithSameSeedAndInputOrderProducesSameTemperatureTraces(int seed)
    {
        var factory = new DeviceScenarioScheduleFactory();
        var firstDevice = CreateDevice(1);
        var secondDevice = CreateDevice(2);
        var devices = new[] { secondDevice, firstDevice };
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition("First"), devices),
            new ResolvedScenario(CreateDefinition("Second", 40), devices)
        };
        var first = factory.Create(resolved, new Random(seed));
        var second = factory.Create(resolved, new Random(seed));

        foreach (var device in devices)
        {
            var firstTrace = ReadTwoScenarioTrace(first[device.Id]);
            var secondTrace = ReadTwoScenarioTrace(second[device.Id]);

            Assert.Equal(firstTrace, secondTrace);
            Assert.InRange(firstTrace[0], 30.0, 34.0);
            Assert.InRange(firstTrace[3], 40.0, 44.0);
            Assert.Equal(22.0, firstTrace[2]);
            Assert.Equal(22.0, firstTrace[5]);
            Assert.Equal(24.0, firstTrace[6]);
        }
    }

    /// <summary>
    /// Verifies that every scenario-device pair selects its own target during
    /// preparation using the supplied random source in plan and device order.
    /// Execution must not draw new random targets.
    /// </summary>
    [Fact]
    public void CreateSelectsTargetsForEveryPairBeforeExecutionUsingSuppliedRandom()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var random = new ScriptedRandom(0.0, 0.25, 0.5, 0.75);
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition("First"), new[] { second, first }),
            new ResolvedScenario(CreateDefinition("Second", 40), new[] { first, second })
        };

        var result = factory.Create(resolved, random);

        Assert.Equal(4, random.CallCount);
        AssertFirstScenarioCompletes(result[first.Id], 31);
        AssertFirstScenarioCompletes(result[second.Id], 30);
        Assert.Equal(42.0, ReadTemperature(result[first.Id], 22, TimeSpan.FromSeconds(120)));
        Assert.Equal(43.0, ReadTemperature(result[second.Id], 22, TimeSpan.FromSeconds(120)));
        Assert.Equal(42.0, ReadTemperature(result[first.Id], 23, TimeSpan.FromSeconds(150)));
        Assert.Equal(43.0, ReadTemperature(result[second.Id], 24, TimeSpan.FromSeconds(150)));
        Assert.Equal(4, random.CallCount);
    }

    /// <summary>
    /// Verifies that preparation preserves input lists and that subsequent
    /// changes to those lists cannot alter already prepared sequences.
    /// </summary>
    [Fact]
    public void CreatePreservesInputsAndDoesNotRetainMutableInputCollections()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var targets = new List<SimulatorDeviceResponse> { device };
        var first = new ResolvedScenario(CreateDefinition("Zeta"), targets);
        var second = new ResolvedScenario(CreateDefinition("Alpha", 40), targets);
        var resolved = new List<ResolvedScenario> { first, second };

        var result = factory.Create(resolved, new FixedRandom());

        Assert.Equal(2, resolved.Count);
        Assert.Same(first, resolved[0]);
        Assert.Same(second, resolved[1]);
        Assert.Same(device, Assert.Single(targets));
        resolved.Clear();
        targets.Clear();

        Assert.Single(result);
        AssertFirstScenarioCompletes(result[device.Id], 32);
        Assert.Equal(42.0, ReadTemperature(result[device.Id], 22, TimeSpan.FromSeconds(120)));
    }

    /// <summary>Verifies that unsupported definitions are rejected regardless of target count.</summary>
    /// <param name="hasDevice">Whether the unsupported scenario has a selected device.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateWithUnsupportedScenarioThrowsNotSupportedException(bool hasDevice)
    {
        var factory = new DeviceScenarioScheduleFactory();
        var devices = hasDevice ? new[] { CreateDevice(1) } : Array.Empty<SimulatorDeviceResponse>();
        var resolved = new[] { new ResolvedScenario(new UnsupportedScenarioDefinition(), devices) };

        var exception = Assert.Throws<NotSupportedException>(() =>
            factory.Create(resolved, new FixedRandom()));

        Assert.Contains(nameof(UnsupportedScenarioDefinition), exception.Message);
    }

    /// <summary>Verifies that an unsupported later entry is not silently omitted from a mixed plan.</summary>
    [Fact]
    public void CreateWithUnsupportedScenarioAfterSupportedScenarioThrows()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var devices = new[] { CreateDevice(1) };
        var resolved = new[]
        {
            new ResolvedScenario(CreateDefinition(), devices),
            new ResolvedScenario(new UnsupportedScenarioDefinition(), devices)
        };

        Assert.Throws<NotSupportedException>(() => factory.Create(resolved, new FixedRandom()));
    }

    /// <summary>Verifies that a runtime's empty-device-identifier failure is propagated.</summary>
    [Fact]
    public void CreateWithEmptyDeviceIdPropagatesArgumentException()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1) with { Id = Guid.Empty };
        var resolved = new[] { new ResolvedScenario(CreateDefinition(), new[] { device }) };

        var exception = Assert.Throws<ArgumentException>(() =>
            factory.Create(resolved, new FixedRandom()));

        Assert.Equal("deviceId", exception.ParamName);
    }

    /// <summary>
    /// Verifies target selection follows plan order before metric sequences
    /// sort scenarios by their configured start delays.
    /// </summary>
    [Fact]
    public void CreateSelectsRandomTargetsBeforeSortingScenarioStartTimes()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var later = CreateDefinition("Later", 40) with { StartsAfter = TimeSpan.FromSeconds(120) };
        var earlier = CreateDefinition("Earlier") with { StartsAfter = TimeSpan.FromSeconds(60) };
        var random = new ScriptedRandom(0.0, 0.75);
        var resolved = new[]
        {
            new ResolvedScenario(later, new[] { device }),
            new ResolvedScenario(earlier, new[] { device })
        };

        var result = factory.Create(resolved, random);
        var schedule = result[device.Id];

        Assert.Equal(2, random.CallCount);
        Assert.Equal(23.0, ReadTemperature(schedule, 23, TimeSpan.FromSeconds(59)));
        Assert.Equal(33.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(60)));
        Assert.Equal(33.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(120)));
        Assert.Equal(22.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(180)));
        Assert.Equal(40.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(181)));
        Assert.Equal(2, random.CallCount);
    }

    /// <summary>
    /// Verifies metrics without a supported scenario pass through unchanged
    /// and cannot advance the prepared temperature queue.
    /// </summary>
    /// <param name="metric">A metric without a runtime created by this factory.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Humidity)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void CreateProducesScheduleThatLeavesOtherMetricsUnchanged(SimulatorTelemetryMetric metric)
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var random = new ScriptedRandom(0.5);
        var result = factory.Create(
            new[] { new ResolvedScenario(CreateDefinition(), new[] { device }) }, random);
        var schedule = result[device.Id];
        var measurement = new SimulatorTelemetryMeasurementRequest(
            Guid.NewGuid(), metric,
            metric == SimulatorTelemetryMetric.Humidity ? 80.0 : null,
            metric == SimulatorTelemetryMetric.Humidity ? null : false,
            new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

        var unchanged = schedule.Apply(measurement, TimeSpan.FromSeconds(600));

        Assert.Equal(device.Id, schedule.DeviceId);
        Assert.Same(measurement, unchanged);
        // The first temperature measurement still starts its own scenario.
        Assert.Equal(32.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(600)));
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>
    /// Creates a scenario with 60 seconds of active time and 60 seconds of
    /// recovery. Large steps expose the selected target on the first measurement.
    /// </summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="minimum">The lower bound of a four-degree abnormal range.</param>
    /// <returns>A valid definition with automatic recovery.</returns>
    private static HighTemperatureScenarioDefinition CreateDefinition(
        string name = "TemperatureScenario", double minimum = 30) =>
        new(
            name,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true,
            TimeSpan.FromSeconds(60),
            minimum,
            minimum + 4,
            100,
            100);

    /// <summary>Creates an active temperature device with a deterministic identifier.</summary>
    /// <param name="number">The suffix used in the device identifier and code.</param>
    /// <returns>A device representing an already resolved compatible target.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number) =>
        new(
            Guid.Parse($"30000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}",
            $"Warehouse device {number}",
            new[] { SimulatorDeviceCapability.Temperature },
            SimulatorDeviceLifecycle.Active);

    /// <summary>Applies a fresh temperature measurement and verifies its identity fields are preserved.</summary>
    /// <param name="schedule">The device schedule to advance.</param>
    /// <param name="normalTemperature">The normal temperature supplied for this measurement.</param>
    /// <param name="elapsed">The shared elapsed simulation time.</param>
    /// <returns>The numeric temperature returned by the prepared schedule.</returns>
    private static double ReadTemperature(DeviceScenarioSchedule schedule, double normalTemperature, TimeSpan elapsed)
    {
        var measurement = new SimulatorTelemetryMeasurementRequest(
            Guid.NewGuid(), SimulatorTelemetryMetric.Temperature, normalTemperature, null,
            new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

        var result = schedule.Apply(measurement, elapsed);

        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.Equal(measurement.Metric, result.Metric);
        Assert.Equal(measurement.MeasuredAtUtc, result.MeasuredAtUtc);
        Assert.Null(result.StateValue);
        return Assert.IsType<double>(result.NumericValue);
    }

    /// <summary>
    /// Verifies the active value, recovery entry, and completion of the first
    /// scenario without advancing to the next scenario within the completion call.
    /// </summary>
    /// <param name="schedule">A fresh schedule whose first temperature scenario starts at zero.</param>
    /// <param name="target">The expected first scenario target.</param>
    private static void AssertFirstScenarioCompletes(
        DeviceScenarioSchedule schedule, double target)
    {
        Assert.Equal(target, ReadTemperature(schedule, 22, TimeSpan.Zero));
        Assert.Equal(target, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(60)));
        Assert.Equal(22.0, ReadTemperature(schedule, 22, TimeSpan.FromSeconds(120)));
    }

    /// <summary>Reads two consecutive scenarios and one normal measurement after completion.</summary>
    /// <param name="schedule">A fresh schedule with two zero-start temperature scenarios.</param>
    /// <returns>The active, recovery-entry, completion, and final normal values.</returns>
    private static double[] ReadTwoScenarioTrace(DeviceScenarioSchedule schedule) =>
        new[]
        {
            ReadTemperature(schedule, 22, TimeSpan.Zero),
            ReadTemperature(schedule, 22, TimeSpan.FromSeconds(60)),
            ReadTemperature(schedule, 22, TimeSpan.FromSeconds(120)),
            ReadTemperature(schedule, 22, TimeSpan.FromSeconds(120)),
            ReadTemperature(schedule, 22, TimeSpan.FromSeconds(180)),
            ReadTemperature(schedule, 22, TimeSpan.FromSeconds(240)),
            ReadTemperature(schedule, 24, TimeSpan.FromSeconds(241))
        };

    /// <summary>Provides an unknown subtype for supported-type guard tests.</summary>
    private sealed record UnsupportedScenarioDefinition() : ScenarioDefinition(
        "UnsupportedScenario",
        TimeSpan.Zero,
        TimeSpan.FromSeconds(60),
        new ScenarioTargetDefinition(ScenarioTargetMode.All),
        true,
        TimeSpan.FromSeconds(60));

    /// <summary>Always selects the midpoint of a scenario's abnormal range.</summary>
    private sealed class FixedRandom : Random
    {
        /// <inheritdoc />
        public override double NextDouble() => 0.5;
    }

    /// <summary>
    /// Supplies an explicit sequence of target-selection samples and rejects
    /// unexpected draws after the configured samples have been consumed.
    /// </summary>
    private sealed class ScriptedRandom : Random
    {
        /// <summary>Stores the remaining samples in their required consumption order.</summary>
        private readonly Queue<double> _samples;

        /// <summary>Creates a random source with a finite script of samples in [0, 1).</summary>
        /// <param name="samples">The samples to return from successive calls.</param>
        public ScriptedRandom(params double[] samples)
        {
            _samples = new Queue<double>(samples);
        }

        /// <summary>Gets the number of requested random samples.</summary>
        public int CallCount { get; private set; }

        /// <summary>Returns the next configured sample.</summary>
        /// <returns>The next value from the sample script.</returns>
        /// <exception cref="InvalidOperationException">Thrown if an unexpected extra draw occurs.</exception>
        public override double NextDouble()
        {
            CallCount++;
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("An unexpected random target was requested.");
            }

            return _samples.Dequeue();
        }
    }
}
