using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies humidity scenario preparation and independent execution of
/// temperature and humidity sequences created by the schedule factory.
/// </summary>
/// <remarks>
/// Complements the existing factory tests rather than replacing them.
/// Tests exercise real schedules through their public measurement API.
/// Inputs represent validated definitions and already resolved devices.
/// No reflection, HTTP requests, real delays, or extra packages are required.
/// </remarks>
public sealed class DeviceScenarioScheduleFactoryHighHumidityTests
{
    /// <summary>Verifies that supported humidity scenarios may have no resolved targets.</summary>
    [Fact]
    public void CreateWithHumidityAndNoDevicesReturnsEmptyDictionaryWithoutUsingRandom()
    {
        var random = new ScriptedRandom();
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), Array.Empty<SimulatorDeviceResponse>())
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        Assert.Empty(result);
        Assert.Equal(0, random.CallCount);
    }

    /// <summary>Verifies that both supported types are accepted in a plan without targets.</summary>
    [Fact]
    public void CreateWithMixedScenariosAndNoDevicesReturnsEmptyDictionary()
    {
        var random = new ScriptedRandom();
        var emptyDevices = Array.Empty<SimulatorDeviceResponse>();
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition(), emptyDevices),
            new ResolvedScenario(CreateHumidityDefinition(), emptyDevices)
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        Assert.Empty(result);
        Assert.Equal(0, random.CallCount);
    }

    /// <summary>Verifies a resolved humidity device receives a working runtime with a retained target.</summary>
    [Fact]
    public void CreateWithOneHumidityScenarioReturnsWorkingScheduleWithRetainedTarget()
    {
        var device = CreateDevice(1);
        var random = new ScriptedRandom(0.25);
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { device })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        var entry = Assert.Single(result);
        Assert.Equal(device.Id, entry.Key);
        Assert.Equal(device.Id, entry.Value.DeviceId);
        Assert.Equal(1, random.CallCount);
        Assert.Equal(91.0, ReadMetric(entry.Value, SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(91.0, ReadMetric(entry.Value, SimulatorTelemetryMetric.Humidity, 82, 30));
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>
    /// Verifies one device receives separate metric sequences that can begin
    /// together, recover together, and return to normal independently.
    /// </summary>
    /// <param name="humidityFirst">Whether humidity appears first in the resolved plan.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateWithBothMetricsOnOneDeviceRunsBothSequences(bool humidityFirst)
    {
        var device = CreateDevice(1);
        var temperature = new ResolvedScenario(CreateTemperatureDefinition(), new[] { device });
        var humidity = new ResolvedScenario(CreateHumidityDefinition(), new[] { device });
        var resolved = humidityFirst ? new[] { humidity, temperature } : new[] { temperature, humidity };
        var random = new ScriptedRandom(0.5, 0.5);

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        var schedule = Assert.Single(result).Value;
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 24, 30));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 30));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 60));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(22.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 24, 120));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 120));
        Assert.Equal(24.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 24, 121));
        Assert.Equal(82.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 121));
        Assert.Equal(2, random.CallCount);
    }

    /// <summary>Verifies partially overlapping target sets attach only the resolved metrics.</summary>
    [Fact]
    public void CreateAssignsOnlyResolvedMetricsToEachDevice()
    {
        var temperatureOnly = CreateDevice(1);
        var shared = CreateDevice(2);
        var humidityOnly = CreateDevice(3);
        var random = new ScriptedRandom(0.5, 0.5, 0.5, 0.5);
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { temperatureOnly, shared }),
            new ResolvedScenario(CreateHumidityDefinition(), new[] { shared, humidityOnly })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        Assert.Equal(3, result.Count);
        Assert.Equal(32.0, ReadMetric(result[temperatureOnly.Id], SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(32.0, ReadMetric(result[shared.Id], SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(92.0, ReadMetric(result[shared.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(92.0, ReadMetric(result[humidityOnly.Id], SimulatorTelemetryMetric.Humidity, 80, 0));

        var normalHumidity = CreateMeasurement(SimulatorTelemetryMetric.Humidity, 80);
        var normalTemperature = CreateMeasurement(SimulatorTelemetryMetric.Temperature, 22);
        Assert.Same(normalHumidity, result[temperatureOnly.Id].Apply(normalHumidity, TimeSpan.Zero));
        Assert.Same(normalTemperature, result[humidityOnly.Id].Apply(normalTemperature, TimeSpan.Zero));
        Assert.Equal(4, random.CallCount);
    }

    /// <summary>
    /// Verifies random targets are selected for every scenario-device pair
    /// in resolved plan order and selected device order, before execution.
    /// </summary>
    [Fact]
    public void CreateSelectsMixedMetricTargetsInPlanAndDeviceOrder()
    {
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var random = new ScriptedRandom(0.0, 0.25, 0.5, 0.75);
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { second, first }),
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { first, second })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);

        Assert.Equal(4, random.CallCount);
        Assert.Equal(91.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(90.0, ReadMetric(result[second.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(32.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(33.0, ReadMetric(result[second.Id], SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(4, random.CallCount);
    }

    /// <summary>
    /// Verifies metric grouping and start-time sorting do not change which
    /// random draw belongs to each resolved scenario.
    /// </summary>
    [Fact]
    public void CreateSelectsTargetsBeforeGroupingMetricsAndSortingStartTimes()
    {
        var device = CreateDevice(1);
        var laterHumidity = CreateHumidityDefinition("LaterHumidity", 94) with
        {
            StartsAfter = TimeSpan.FromSeconds(120)
        };
        var earlierHumidity = CreateHumidityDefinition("EarlierHumidity") with
        {
            StartsAfter = TimeSpan.FromSeconds(60)
        };
        var random = new ScriptedRandom(0.0, 0.25, 0.75);
        var resolved = new[]
        {
            new ResolvedScenario(laterHumidity, new[] { device }),
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { device }),
            new ResolvedScenario(earlierHumidity, new[] { device })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);
        var schedule = result[device.Id];

        Assert.Equal(3, random.CallCount);
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 59));
        Assert.Equal(93.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(31.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 60));
        Assert.Equal(93.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 180));
        Assert.Equal(94.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 181));
        Assert.Equal(94.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 241));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 301));
        Assert.Equal(3, random.CallCount);
    }

    /// <summary>
    /// Verifies equal-start humidity scenarios retain plan order and the next
    /// scenario begins on a subsequent measurement after completion.
    /// </summary>
    /// <param name="reverse">Whether the scenario with the higher target appears first.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatePreservesPlanOrderForEqualHumidityStartTimes(bool reverse)
    {
        var device = CreateDevice(1);
        var lower = new ResolvedScenario(CreateHumidityDefinition("Zeta"), new[] { device });
        var higher = new ResolvedScenario(CreateHumidityDefinition("Alpha", 94), new[] { device });
        var resolved = reverse ? new[] { higher, lower } : new[] { lower, higher };
        var random = new ScriptedRandom(0.5, 0.5);

        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);
        var schedule = result[device.Id];

        AssertMetricScenarioCompletes(schedule, SimulatorTelemetryMetric.Humidity, 80,
            reverse ? 96 : 92);
        Assert.Equal(reverse ? 92.0 : 96.0,
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120));
        Assert.Equal(2, random.CallCount);
    }

    /// <summary>
    /// Verifies a queued humidity scenario waits for recovery and retains its
    /// full active duration when its planned start has already elapsed.
    /// </summary>
    [Fact]
    public void CreateQueuedHumidityScenarioStartsAfterRecoveryWithItsFullDuration()
    {
        var device = CreateDevice(1);
        var second = CreateHumidityDefinition("SecondHumidity", 94) with
        {
            StartsAfter = TimeSpan.FromSeconds(30)
        };
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition("FirstHumidity"), new[] { device }),
            new ResolvedScenario(second, new[] { device })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));
        var schedule = result[device.Id];

        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 30));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120));
        Assert.Equal(96.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120));
        Assert.Equal(96.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 179));
        Assert.Equal(96.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 180));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 240));
        Assert.Equal(82.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 241));
    }

    /// <summary>Verifies each metric sequence uses its own start and recovery timeline.</summary>
    [Fact]
    public void CreateWithDifferentMetricStartTimesKeepsTimelinesIndependent()
    {
        var device = CreateDevice(1);
        var humidity = CreateHumidityDefinition() with { StartsAfter = TimeSpan.FromSeconds(30) };
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { device }),
            new ResolvedScenario(humidity, new[] { device })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));
        var schedule = result[device.Id];

        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 30));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 30));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 60));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(27.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 90));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 90));
        Assert.Equal(22.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 120));
        Assert.Equal(86.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 120));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 150));
    }

    /// <summary>
    /// Verifies completion of one metric cannot advance another metric that
    /// has not yet produced its first measurement.
    /// </summary>
    /// <param name="completedMetric">The metric whose scenario finishes first.</param>
    /// <param name="untouchedMetric">The metric that has not yet been measured.</param>
    /// <param name="completedNormal">The normal value used during the first scenario.</param>
    /// <param name="untouchedNormal">The normal value for the late-starting metric.</param>
    /// <param name="completedTarget">The first scenario's expected target.</param>
    /// <param name="untouchedTarget">The late-starting scenario's expected target.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature, SimulatorTelemetryMetric.Humidity, 22.0, 80.0, 32.0, 92.0)]
    [InlineData(SimulatorTelemetryMetric.Humidity, SimulatorTelemetryMetric.Temperature, 80.0, 22.0, 92.0, 32.0)]
    public void CreateKeepsMetricCompletionIndependent(
        SimulatorTelemetryMetric completedMetric, SimulatorTelemetryMetric untouchedMetric,
        double completedNormal, double untouchedNormal,
        double completedTarget, double untouchedTarget)
    {
        var device = CreateDevice(1);
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { device }),
            new ResolvedScenario(CreateHumidityDefinition(), new[] { device })
        };
        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));
        var schedule = result[device.Id];

        AssertMetricScenarioCompletes(schedule, completedMetric, completedNormal, completedTarget);
        Assert.Equal(completedNormal + 1, ReadMetric(schedule, completedMetric, completedNormal + 1, 121));
        Assert.Equal(untouchedTarget, ReadMetric(schedule, untouchedMetric, untouchedNormal, 121));
        Assert.Equal(untouchedTarget, ReadMetric(schedule, untouchedMetric, untouchedNormal, 181));
        Assert.Equal(untouchedNormal, ReadMetric(schedule, untouchedMetric, untouchedNormal, 241));
    }

    /// <summary>Verifies advancing one device does not change another device's humidity state.</summary>
    [Fact]
    public void CreateKeepsHumidityStateIndependentBetweenDevices()
    {
        var first = CreateDevice(1);
        var second = CreateDevice(2);
        var definition = CreateHumidityDefinition() with { MaximumRisePerMeasurement = 2 };
        var resolved = new[] { new ResolvedScenario(definition, new[] { first, second }) };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));

        Assert.NotSame(result[first.Id], result[second.Id]);
        Assert.Equal(82.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(84.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Humidity, 80, 30));
        Assert.Equal(80.0, ReadMetric(result[second.Id], SimulatorTelemetryMetric.Humidity, 78, 30));
        Assert.Equal(86.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Humidity, 80, 40));
        Assert.Equal(82.0, ReadMetric(result[second.Id], SimulatorTelemetryMetric.Humidity, 78, 40));
    }

    /// <summary>Verifies every preparation creates fresh state for both supported metrics.</summary>
    [Fact]
    public void CreateOnRepeatedCallsCreatesIndependentStateForBothMetrics()
    {
        var factory = new DeviceScenarioScheduleFactory();
        var device = CreateDevice(1);
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition() with { MaximumRisePerMeasurement = 2 }, new[] { device }),
            new ResolvedScenario(CreateHumidityDefinition() with { MaximumRisePerMeasurement = 2 }, new[] { device })
        };
        var first = factory.Create(resolved, new ScriptedRandom(0.5, 0.5));
        Assert.Equal(22.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Temperature, 20, 0));
        Assert.Equal(82.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(24.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Temperature, 20, 30));
        Assert.Equal(84.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Humidity, 80, 30));

        var second = factory.Create(resolved, new ScriptedRandom(0.5, 0.5));

        Assert.NotSame(first, second);
        Assert.NotSame(first[device.Id], second[device.Id]);
        Assert.Equal(22.0, ReadMetric(second[device.Id], SimulatorTelemetryMetric.Temperature, 20, 30));
        Assert.Equal(82.0, ReadMetric(second[device.Id], SimulatorTelemetryMetric.Humidity, 80, 30));
        Assert.Equal(26.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Temperature, 20, 40));
        Assert.Equal(86.0, ReadMetric(first[device.Id], SimulatorTelemetryMetric.Humidity, 80, 40));
    }

    /// <summary>Verifies reproducibility across both metrics, two devices, and queued scenarios.</summary>
    /// <param name="seed">The seed shared by two fresh random sources.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-17)]
    public void CreateWithEqualSeedsProducesEqualMixedMetricTraces(int seed)
    {
        var firstDevice = CreateDevice(1);
        var secondDevice = CreateDevice(2);
        var devices = new[] { secondDevice, firstDevice };
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition("FirstHumidity"), devices),
            new ResolvedScenario(CreateTemperatureDefinition("FirstTemperature"), devices),
            new ResolvedScenario(CreateTemperatureDefinition("SecondTemperature", 40), devices),
            new ResolvedScenario(CreateHumidityDefinition("SecondHumidity", 94), devices)
        };
        var factory = new DeviceScenarioScheduleFactory();

        var first = factory.Create(resolved, new Random(seed));
        var second = factory.Create(resolved, new Random(seed));

        foreach (var device in devices)
        {
            var firstTrace = ReadMixedMetricTrace(first[device.Id]);
            var secondTrace = ReadMixedMetricTrace(second[device.Id]);
            Assert.Equal(firstTrace, secondTrace);
            Assert.InRange(firstTrace[0], 30.0, 34.0);
            Assert.InRange(firstTrace[1], 90.0, 94.0);
            Assert.InRange(firstTrace[6], 40.0, 44.0);
            Assert.InRange(firstTrace[7], 94.0, 98.0);
            Assert.Equal(22.0, firstTrace[4]);
            Assert.Equal(80.0, firstTrace[5]);
            Assert.Equal(22.0, firstTrace[10]);
            Assert.Equal(80.0, firstTrace[11]);
            Assert.Equal(24.0, firstTrace[12]);
            Assert.Equal(82.0, firstTrace[13]);
        }
    }

    /// <summary>Verifies selected humidity devices are grouped by identity rather than code.</summary>
    [Fact]
    public void CreateGroupsHumidityDevicesByIdInsteadOfCode()
    {
        var first = CreateDevice(1);
        var second = CreateDevice(2) with { Code = first.Code };
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { first, second })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.25, 0.75));

        Assert.Equal(2, result.Count);
        Assert.Equal(91.0, ReadMetric(result[first.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(93.0, ReadMetric(result[second.Id], SimulatorTelemetryMetric.Humidity, 80, 0));
    }

    /// <summary>Verifies distinct response objects with one identifier share one mixed-metric schedule.</summary>
    [Fact]
    public void CreateGroupsBothMetricsForDifferentResponsesWithTheSameDeviceId()
    {
        var device = CreateDevice(1);
        var anotherResponse = device with { Name = "Updated name" };
        var resolved = new[]
        {
            new ResolvedScenario(CreateTemperatureDefinition(), new[] { device }),
            new ResolvedScenario(CreateHumidityDefinition(), new[] { anotherResponse })
        };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));

        var entry = Assert.Single(result);
        Assert.Equal(device.Id, entry.Key);
        Assert.Equal(32.0, ReadMetric(entry.Value, SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(92.0, ReadMetric(entry.Value, SimulatorTelemetryMetric.Humidity, 80, 0));
    }

    /// <summary>Verifies a selected humidity device must have a nonempty identifier.</summary>
    [Fact]
    public void CreateWithEmptyHumidityDeviceIdPropagatesArgumentException()
    {
        var device = CreateDevice(1) with { Id = Guid.Empty };
        var random = new ScriptedRandom();
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { device })
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new DeviceScenarioScheduleFactory().Create(resolved, random));

        Assert.Equal("deviceId", exception.ParamName);
        Assert.Equal(0, random.CallCount);
    }

    /// <summary>Verifies unknown types remain unsupported after a valid humidity entry, even without targets.</summary>
    [Fact]
    public void CreateWithUnsupportedScenarioAfterHumidityThrowsEvenWithoutTargets()
    {
        var device = CreateDevice(1);
        var random = new ScriptedRandom(0.5);
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { device }),
            new ResolvedScenario(new UnsupportedScenarioDefinition(), Array.Empty<SimulatorDeviceResponse>())
        };

        var exception = Assert.Throws<NotSupportedException>(() =>
            new DeviceScenarioScheduleFactory().Create(resolved, random));

        Assert.Contains(nameof(UnsupportedScenarioDefinition), exception.Message);
    }

    /// <summary>Verifies prepared mixed-metric schedules do not retain mutable input collections.</summary>
    [Fact]
    public void CreatePreservesInputsAndCopiesPreparedMixedMetricQueues()
    {
        var device = CreateDevice(1);
        var targets = new List<SimulatorDeviceResponse> { device };
        var humidity = new ResolvedScenario(CreateHumidityDefinition(), targets);
        var temperature = new ResolvedScenario(CreateTemperatureDefinition(), targets);
        var resolved = new List<ResolvedScenario> { humidity, temperature };

        var result = new DeviceScenarioScheduleFactory().Create(resolved, new ScriptedRandom(0.5, 0.5));

        Assert.Equal(2, resolved.Count);
        Assert.Same(humidity, resolved[0]);
        Assert.Same(temperature, resolved[1]);
        Assert.Same(device, Assert.Single(targets));
        resolved.Clear();
        targets.Clear();

        var schedule = Assert.Single(result).Value;
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 0));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 0));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(32.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 60));
        Assert.Equal(80.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120));
        Assert.Equal(22.0, ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 120));
    }

    /// <summary>Verifies a humidity-only schedule leaves unrelated metrics and its own queue unchanged.</summary>
    /// <param name="metric">A metric with no scenario in the prepared schedule.</param>
    [Theory]
    [InlineData(SimulatorTelemetryMetric.Temperature)]
    [InlineData(SimulatorTelemetryMetric.DoorState)]
    [InlineData(SimulatorTelemetryMetric.LightState)]
    public void CreateWithOnlyHumidityLeavesOtherMetricsUnchanged(SimulatorTelemetryMetric metric)
    {
        var device = CreateDevice(1);
        var random = new ScriptedRandom(0.5);
        var resolved = new[]
        {
            new ResolvedScenario(CreateHumidityDefinition(), new[] { device })
        };
        var result = new DeviceScenarioScheduleFactory().Create(resolved, random);
        var schedule = result[device.Id];
        var measurement = new SimulatorTelemetryMeasurementRequest(
            Guid.NewGuid(), metric,
            metric == SimulatorTelemetryMetric.Temperature ? 22.0 : null,
            metric == SimulatorTelemetryMetric.Temperature ? null : false,
            new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc));

        var resultMeasurement = schedule.Apply(measurement, TimeSpan.FromSeconds(600));

        Assert.Same(measurement, resultMeasurement);
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 600));
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>Verifies the prepared humidity sequence waits for its configured start delay.</summary>
    [Fact]
    public void CreateProducesHumiditySequenceThatWaitsForItsStartDelay()
    {
        var device = CreateDevice(1);
        var definition = CreateHumidityDefinition() with { StartsAfter = TimeSpan.FromSeconds(60) };
        var random = new ScriptedRandom(0.5);
        var result = new DeviceScenarioScheduleFactory().Create(
            new[] { new ResolvedScenario(definition, new[] { device }) }, random);
        var schedule = result[device.Id];
        var pendingMeasurement = CreateMeasurement(SimulatorTelemetryMetric.Humidity, 80);

        Assert.Equal(1, random.CallCount);
        Assert.Same(pendingMeasurement, schedule.Apply(pendingMeasurement, TimeSpan.FromSeconds(59)));
        Assert.Equal(92.0, ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60));
        Assert.Equal(1, random.CallCount);
    }

    /// <summary>Creates a humidity scenario with a four-point abnormal range and large step limits.</summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="minimum">The lower abnormal bound; fixtures keep the upper bound at most 100.</param>
    /// <returns>A definition with 60 seconds of active time and 60 seconds of recovery.</returns>
    private static HighHumidityScenarioDefinition CreateHumidityDefinition(
        string name = "HumidityScenario", double minimum = 90) =>
        new(name, TimeSpan.Zero, TimeSpan.FromSeconds(60),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true,
            TimeSpan.FromSeconds(60), minimum, minimum + 4, 100, 100);

    /// <summary>Creates a temperature scenario with a four-degree range and large step limits.</summary>
    /// <param name="name">The scenario name.</param>
    /// <param name="minimum">The lower abnormal temperature bound.</param>
    /// <returns>A definition with 60 seconds of active time and 60 seconds of recovery.</returns>
    private static HighTemperatureScenarioDefinition CreateTemperatureDefinition(
        string name = "TemperatureScenario", double minimum = 30) =>
        new(name, TimeSpan.Zero, TimeSpan.FromSeconds(60),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true,
            TimeSpan.FromSeconds(60), minimum, minimum + 4, 100, 100);

    /// <summary>Creates an active device compatible with both numeric scenario types.</summary>
    /// <param name="number">The suffix used in its identifier and code.</param>
    /// <returns>An already resolved compatible device.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number) =>
        new(Guid.Parse($"40000000-0000-0000-0000-{number:D12}"),
            $"WH-{number:D3}", $"Warehouse device {number}",
            new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity },
            SimulatorDeviceLifecycle.Active);

    /// <summary>Creates a fresh numeric measurement with a UTC timestamp.</summary>
    /// <param name="metric">The temperature or humidity metric.</param>
    /// <param name="normalValue">The numeric value before applying a scenario.</param>
    /// <returns>The measurement to transform.</returns>
    private static SimulatorTelemetryMeasurementRequest CreateMeasurement(
        SimulatorTelemetryMetric metric, double normalValue) =>
        new(Guid.NewGuid(), metric, normalValue, null,
            new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc).AddTicks(1234567));

    /// <summary>Applies a new measurement and verifies preservation of its metadata and input value.</summary>
    /// <param name="schedule">The schedule to advance.</param>
    /// <param name="metric">The numeric metric to process.</param>
    /// <param name="normalValue">The measurement's original numeric value.</param>
    /// <param name="elapsedSeconds">The shared elapsed simulation time in seconds.</param>
    /// <returns>The outgoing numeric value.</returns>
    private static double ReadMetric(DeviceScenarioSchedule schedule,
        SimulatorTelemetryMetric metric, double normalValue, double elapsedSeconds)
    {
        var measurement = CreateMeasurement(metric, normalValue);

        var result = schedule.Apply(measurement, TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(measurement.MeasurementId, result.MeasurementId);
        Assert.Equal(measurement.Metric, result.Metric);
        Assert.Equal(measurement.MeasuredAtUtc, result.MeasuredAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.MeasuredAtUtc.Kind);
        Assert.Equal(measurement.StateValue, result.StateValue);
        Assert.Equal(normalValue, Assert.IsType<double>(measurement.NumericValue));
        return Assert.IsType<double>(result.NumericValue);
    }

    /// <summary>Completes the first scenario without advancing a queued scenario in the same call.</summary>
    /// <param name="schedule">The schedule containing the metric sequence.</param>
    /// <param name="metric">The metric whose scenario is completed.</param>
    /// <param name="normalValue">The normal input and expected recovery target.</param>
    /// <param name="target">The expected abnormal target.</param>
    private static void AssertMetricScenarioCompletes(DeviceScenarioSchedule schedule,
        SimulatorTelemetryMetric metric, double normalValue, double target)
    {
        Assert.Equal(target, ReadMetric(schedule, metric, normalValue, 0));
        Assert.Equal(target, ReadMetric(schedule, metric, normalValue, 60));
        Assert.Equal(normalValue, ReadMetric(schedule, metric, normalValue, 120));
    }

    /// <summary>Reads both metric queues through two complete scenarios and normal operation.</summary>
    /// <param name="schedule">A fresh schedule with two zero-start scenarios for each numeric metric.</param>
    /// <returns>Interleaved temperature and humidity values for comparison.</returns>
    private static double[] ReadMixedMetricTrace(DeviceScenarioSchedule schedule) =>
        new[]
        {
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 0),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 0),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 60),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 60),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 120),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 120),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 120),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 180),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 180),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 22, 240),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 80, 240),
            ReadMetric(schedule, SimulatorTelemetryMetric.Temperature, 24, 241),
            ReadMetric(schedule, SimulatorTelemetryMetric.Humidity, 82, 241)
        };

    /// <summary>Represents a type for which the factory must not create a runtime.</summary>
    private sealed record UnsupportedScenarioDefinition() : ScenarioDefinition(
        "UnsupportedScenario", TimeSpan.Zero, TimeSpan.FromSeconds(60),
        new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromSeconds(60));

    /// <summary>Supplies controlled fractions and rejects unexpected random draws.</summary>
    private sealed class ScriptedRandom : Random
    {
        private readonly Queue<double> _samples;

        /// <summary>Initializes the samples available to factory preparation.</summary>
        /// <param name="samples">Random fractions in the interval [0, 1).</param>
        public ScriptedRandom(params double[] samples)
        {
            _samples = new Queue<double>(samples);
        }

        /// <summary>Gets the number of supplied samples.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        public override double NextDouble()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("An unexpected random target was requested.");
            }

            CallCount++;
            return _samples.Dequeue();
        }
    }
}
