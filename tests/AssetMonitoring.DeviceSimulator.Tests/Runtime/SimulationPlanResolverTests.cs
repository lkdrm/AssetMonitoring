using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies plan-level scenario resolution, ordering, random-source ownership,
/// and failure propagation using the production target resolver.
/// </summary>
/// <remarks>
/// These synchronous tests require no HTTP client, clock, or additional package.
/// ScenarioTargetResolver is exercised as a real dependency; its individual
/// selection rules are covered separately by ScenarioTargetResolverTests.
/// </remarks>
public sealed class SimulationPlanResolverTests
{
    /// <summary>Verifies that the target resolver dependency is required.</summary>
    [Fact]
    public void ConstructorWithNullTargetResolverThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new SimulationPlanResolver(null!));

        Assert.Equal("targetResolver", exception.ParamName);
    }

    /// <summary>Verifies that both operation arguments are required.</summary>
    /// <param name="argument">The argument to omit.</param>
    [Theory]
    [InlineData("plan")]
    [InlineData("devices")]
    public void ResolveWithNullArgumentThrowsArgumentNullException(string argument)
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var plan = new SimulationPlanDefinition(
            "NormalOperation", 42, Array.Empty<ScenarioDefinition>());

        var exception = Assert.Throws<ArgumentNullException>(() => resolver.Resolve(
            argument == "plan" ? null! : plan,
            argument == "devices" ? null! : CreateDevices()));

        Assert.Equal(argument, exception.ParamName);
    }

    /// <summary>
    /// Verifies that normal operation with no scenarios resolves to an empty
    /// collection, regardless of whether any devices were supplied.
    /// </summary>
    /// <param name="emptyDevices">Whether the device collection is empty.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveWithNoScenariosReturnsEmptyCollection(bool emptyDevices)
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var plan = new SimulationPlanDefinition(
            "NormalOperation", 42, Array.Empty<ScenarioDefinition>());
        var devices = emptyDevices ? Array.Empty<SimulatorDeviceResponse>() : CreateDevices();

        var resolved = resolver.Resolve(plan, devices);

        Assert.Empty(resolved);
    }

    /// <summary>
    /// Verifies that all target modes are associated with their original
    /// definitions in plan order, without sorting by start time or modifying inputs.
    /// </summary>
    [Fact]
    public void ResolveAssociatesSelectedDevicesWithDefinitionsInPlanOrder()
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var all = CreateScenario("AllSensors", new ScenarioTargetDefinition(ScenarioTargetMode.All))
            with
        { StartsAfter = TimeSpan.FromMinutes(10) };
        var specific = CreateScenario("NorthSensor",
            new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: "WH-002"))
            with
        { StartsAfter = TimeSpan.Zero };
        var random = CreateScenario("RandomSensors",
            new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 2))
            with
        { StartsAfter = TimeSpan.FromMinutes(5) };
        var scenarios = new List<ScenarioDefinition> { all, specific, random };
        var originalScenarios = scenarios.ToArray();
        var devices = CreateDevices().ToList();
        var originalDevices = devices.ToArray();
        var originalCapabilities = devices.Select(x => x.Capabilities.ToArray()).ToArray();
        var plan = new SimulationPlanDefinition("MixedPlan", 42, scenarios);

        var resolved = resolver.Resolve(plan, devices);

        Assert.Equal(3, resolved.Count);
        Assert.Same(all, resolved[0].Definition);
        Assert.Same(specific, resolved[1].Definition);
        Assert.Same(random, resolved[2].Definition);
        Assert.Equal(new[] { "WH-001", "WH-002", "WH-003", "WH-004", "WH-005" },
            resolved[0].Devices.Select(x => x.Code));
        Assert.Same(devices[2], Assert.Single(resolved[1].Devices));
        Assert.Equal(2, resolved[2].Devices.Count);
        Assert.Equal(2, resolved[2].Devices.Select(x => x.Id).Distinct().Count());
        var eligibleIds = devices.Take(5).Select(x => x.Id).ToHashSet();
        Assert.All(resolved[2].Devices, device => Assert.Contains(device.Id, eligibleIds));

        Assert.Equal(originalScenarios.Length, scenarios.Count);
        for (var index = 0; index < originalScenarios.Length; index++)
        {
            Assert.Same(originalScenarios[index], scenarios[index]);
        }
        Assert.Equal(originalDevices.Length, devices.Count);
        for (var index = 0; index < originalDevices.Length; index++)
        {
            Assert.Same(originalDevices[index], devices[index]);
            Assert.Equal(originalCapabilities[index], devices[index].Capabilities);
        }
    }

    /// <summary>
    /// Verifies that the plan seed initializes one random source shared across
    /// target resolutions, and resolving the plan again starts from that seed again.
    /// </summary>
    /// <param name="seed">The plan seed, including zero and a negative value.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-17)]
    public void ResolveSharesPlanRandomSourceAndReplaysSeededSelection(int seed)
    {
        var targetResolver = new ScenarioTargetResolver();
        var resolver = new SimulationPlanResolver(targetResolver);
        var first = CreateScenario("FirstSelection",
            new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 2));
        var second = CreateScenario("SecondSelection",
            new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 3))
            with
        { StartsAfter = TimeSpan.FromMinutes(5) };
        var plan = new SimulationPlanDefinition("SeededPlan", seed, new ScenarioDefinition[] { first, second });
        var devices = CreateDevices();

        // Establish the target resolver's expected behavior with one shared source.
        // This avoids duplicating its selection algorithm or hard-coding random IDs.
        var referenceRandom = new Random(seed);
        var expectedFirst = targetResolver.Resolve(first, devices, referenceRandom);
        var expectedSecond = targetResolver.Resolve(second, devices, referenceRandom);

        var resolved = resolver.Resolve(plan, devices);
        var replayed = resolver.Resolve(plan, devices);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(2, replayed.Count);
        Assert.Same(first, resolved[0].Definition);
        Assert.Same(second, resolved[1].Definition);
        Assert.Equal(expectedFirst.Select(x => x.Id), resolved[0].Devices.Select(x => x.Id));
        Assert.Equal(expectedSecond.Select(x => x.Id), resolved[1].Devices.Select(x => x.Id));
        Assert.Equal(resolved[0].Devices.Select(x => x.Id), replayed[0].Devices.Select(x => x.Id));
        Assert.Equal(resolved[1].Devices.Select(x => x.Id), replayed[1].Devices.Select(x => x.Id));
    }

    /// <summary>
    /// Verifies that a plan without a seed still supports valid random selection.
    /// No particular selection or difference between runs is required.
    /// </summary>
    [Fact]
    public void ResolveWithNoSeedReturnsRequestedCompatibleDevices()
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var scenario = CreateScenario("UnseededSelection",
            new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 3));
        var plan = new SimulationPlanDefinition("UnseededPlan", null, new ScenarioDefinition[] { scenario });
        var devices = CreateDevices();
        var eligibleIds = devices.Take(5).Select(x => x.Id).ToHashSet();

        var resolved = Assert.Single(resolver.Resolve(plan, devices));

        Assert.Same(scenario, resolved.Definition);
        Assert.Equal(3, resolved.Devices.Count);
        Assert.Equal(3, resolved.Devices.Select(x => x.Id).Distinct().Count());
        Assert.All(resolved.Devices, device => Assert.Contains(device.Id, eligibleIds));
    }

    /// <summary>
    /// Verifies that a later target-resolution failure propagates instead of
    /// returning a partial plan or skipping the failed scenario.
    /// </summary>
    /// <param name="failureKind">The expected target-resolution failure category.</param>
    [Theory]
    [InlineData("arguments")]
    [InlineData("availability")]
    [InlineData("unsupported")]
    public void ResolveWithLaterScenarioFailurePropagatesException(string failureKind)
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var valid = CreateScenario("ValidFirst", new ScenarioTargetDefinition(ScenarioTargetMode.All));
        ScenarioDefinition invalid = failureKind switch
        {
            "arguments" => CreateScenario("InvalidCount",
                new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 0)),
            "availability" => CreateScenario("UnavailableTarget",
                new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: "MISSING-DEVICE")),
            "unsupported" => new UnsupportedScenarioDefinition(),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var plan = new SimulationPlanDefinition(
            "FailurePlan", 42, new ScenarioDefinition[] { valid, invalid });

        var exception = Record.Exception(() => resolver.Resolve(plan, CreateDevices()));

        switch (failureKind)
        {
            case "arguments":
                Assert.Equal("scenario", Assert.IsType<ArgumentException>(exception).ParamName);
                break;
            case "availability":
                var unavailable = Assert.IsType<InvalidOperationException>(exception);
                Assert.Contains("MISSING-DEVICE", unavailable.Message);
                Assert.Contains(invalid.Name, unavailable.Message);
                break;
            case "unsupported":
                Assert.Contains(nameof(UnsupportedScenarioDefinition),
                    Assert.IsType<NotSupportedException>(exception).Message);
                break;
        }
    }

    /// <summary>
    /// Verifies that the same resolver instance can handle independent plans
    /// and device collections without retaining data from a previous operation.
    /// </summary>
    [Fact]
    public void ResolveWithDifferentPlansUsesCurrentPlanAndDevices()
    {
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());
        var firstScenario = CreateScenario("FirstPlanScenario", new ScenarioTargetDefinition(ScenarioTargetMode.All));
        var firstPlan = new SimulationPlanDefinition("FirstPlan", 42, new ScenarioDefinition[] { firstScenario });
        var firstDevices = CreateDevices();
        var firstResolved = resolver.Resolve(firstPlan, firstDevices);

        var secondDevice = CreateDevice(100, "SECOND-001");
        var secondScenario = CreateScenario("SecondPlanScenario",
            new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: secondDevice.Code));
        var secondPlan = new SimulationPlanDefinition("SecondPlan", 7, new ScenarioDefinition[] { secondScenario });
        var secondResolved = resolver.Resolve(secondPlan, new[] { secondDevice });

        Assert.Same(firstScenario, Assert.Single(firstResolved).Definition);
        Assert.Equal(5, firstResolved[0].Devices.Count);
        Assert.Same(secondScenario, Assert.Single(secondResolved).Definition);
        Assert.Same(secondDevice, Assert.Single(secondResolved[0].Devices));
        Assert.DoesNotContain(firstResolved[0].Devices, device => device.Id == secondDevice.Id);
    }

    /// <summary>Creates a high-temperature scenario with valid timing and values.</summary>
    /// <param name="name">The scenario name used to check identity and order.</param>
    /// <param name="target">The target definition used for device selection.</param>
    /// <returns>The scenario definition supplied to the plan resolver.</returns>
    private static HighTemperatureScenarioDefinition CreateScenario(string name, ScenarioTargetDefinition target) =>
        new(
            name,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1),
            target,
            true,
            TimeSpan.FromMinutes(1),
            30,
            35,
            2,
            2);

    /// <summary>
    /// Creates a deliberately unsorted catalog whose first five devices are
    /// compatible; the final retired and humidity-only devices are ineligible.
    /// </summary>
    /// <returns>The device collection used by plan-level tests.</returns>
    private static SimulatorDeviceResponse[] CreateDevices() =>
        new[]
        {
            CreateDevice(5, "WH-005"),
            CreateDevice(1, "WH-001"),
            CreateDevice(2, "WH-002"),
            CreateDevice(4, "WH-004"),
            CreateDevice(3, "WH-003"),
            CreateDevice(6, "WH-006") with { Lifecycle = SimulatorDeviceLifecycle.Retired },
            CreateDevice(7, "WH-007") with { Capabilities = new[] { SimulatorDeviceCapability.Humidity } }
        };

    /// <summary>Creates an active temperature device with deterministic identity.</summary>
    /// <param name="number">The numeric suffix used for the device identifier.</param>
    /// <param name="code">The device catalog code.</param>
    /// <returns>An active device supporting temperature and humidity.</returns>
    private static SimulatorDeviceResponse CreateDevice(int number, string code) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            code,
            $"Warehouse Device {number}",
            new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity },
            SimulatorDeviceLifecycle.Active);

    /// <summary>Provides a scenario type unsupported by the real target resolver.</summary>
    private sealed record UnsupportedScenarioDefinition() : ScenarioDefinition(
        "UnsupportedScenario",
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1),
        new ScenarioTargetDefinition(ScenarioTargetMode.All),
        false,
        null);
}
