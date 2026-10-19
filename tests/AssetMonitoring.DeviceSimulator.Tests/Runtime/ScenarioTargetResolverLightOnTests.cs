using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies light-state compatibility, all target modes, seeded selection,
/// and mixed-plan resolution through production resolver services.
/// </summary>
public sealed class ScenarioTargetResolverLightOnTests
{
    /// <summary>
    /// Verifies that All returns only active light-capable devices in ordinal
    /// code order, including light-only and multi-capability devices.
    /// </summary>
    [Fact]
    public void ResolveAllForLightOnReturnsOnlyActiveLightDevicesInOrdinalOrder()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();

        var selected = resolver.Resolve(CreateScenario(), devices, new Random(42));

        Assert.Equal(new[] { "LT-002", "LT-010", "lt-003" },
            selected.Select(device => device.Code));
        Assert.Equal(3, selected.Count);
        Assert.Same(devices[1], selected[0]);
        Assert.Same(devices[0], selected[1]);
        Assert.Same(devices[2], selected[2]);
    }

    /// <summary>
    /// Verifies exact-code targeting returns the original active device
    /// supporting light state.
    /// </summary>
    /// <param name="deviceCode">The exact compatible device code.</param>
    /// <param name="index">The device's position in the unsorted fixture.</param>
    [Theory]
    [InlineData("LT-010", 0)]
    [InlineData("LT-002", 1)]
    [InlineData("lt-003", 2)]
    public void ResolveSpecificLightDeviceReturnsRequestedOriginalDevice(string deviceCode, int index)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(
            ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var selected = resolver.Resolve(scenario, devices, new Random(42));

        Assert.Same(devices[index], Assert.Single(selected));
    }

    /// <summary>
    /// Verifies that a specific light target must be active and light-capable
    /// even when other eligible devices are present.
    /// </summary>
    /// <param name="deviceCode">The existing but unavailable or incompatible device code.</param>
    [Theory]
    [InlineData("LT-004")]
    [InlineData("LT-005")]
    [InlineData("LT-006")]
    [InlineData("LT-007")]
    [InlineData("LT-008")]
    [InlineData("LT-009")]
    [InlineData("LT-011")]
    public void ResolveSpecificLightDeviceWithIncompatibleTargetThrows(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(
            ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies that light target codes must match exactly, including case
    /// and surrounding whitespace.
    /// </summary>
    /// <param name="deviceCode">The absent or inexact device code.</param>
    [Theory]
    [InlineData("LT-999")]
    [InlineData("lt-002")]
    [InlineData(" LT-002 ")]
    public void ResolveSpecificLightDeviceWithoutExactCodeMatchThrows(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(
            ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies that every target mode rejects an empty catalog or a catalog
    /// without active light devices, despite other active telemetry capabilities.
    /// </summary>
    /// <param name="mode">The target mode under test.</param>
    /// <param name="emptyCatalog">Whether to supply an empty device catalog.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All, true)]
    [InlineData(ScenarioTargetMode.All, false)]
    [InlineData(ScenarioTargetMode.SpecificDevice, true)]
    [InlineData(ScenarioTargetMode.SpecificDevice, false)]
    [InlineData(ScenarioTargetMode.RandomCompatible, true)]
    [InlineData(ScenarioTargetMode.RandomCompatible, false)]
    public void ResolveLightOnWithoutCompatibleDevicesThrows(
        ScenarioTargetMode mode, bool emptyCatalog)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenarioForMode(mode);
        var devices = emptyCatalog
            ? Array.Empty<SimulatorDeviceResponse>()
            : CreateDevices().Skip(3).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, devices, new Random(42)));

        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies that random light targeting returns the requested number
    /// of distinct eligible devices from the original catalog.
    /// </summary>
    /// <param name="count">The number of compatible devices to select.</param>
    /// <param name="seed">The seed supplied to the random source.</param>
    [Theory]
    [InlineData(1, 42)]
    [InlineData(2, 42)]
    [InlineData(3, 42)]
    [InlineData(2, 0)]
    [InlineData(2, -17)]
    public void ResolveRandomLightReturnsRequestedDistinctEligibleDevices(int count, int seed)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var eligibleIds = devices.Take(3).Select(device => device.Id).ToHashSet();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: count);

        var selected = resolver.Resolve(scenario, devices, new Random(seed));

        Assert.Equal(count, selected.Count);
        Assert.Equal(count, selected.Select(device => device.Id).Distinct().Count());
        Assert.All(selected, device =>
        {
            Assert.Contains(device.Id, eligibleIds);
            Assert.Equal(SimulatorDeviceLifecycle.Active, device.Lifecycle);
            Assert.Contains(SimulatorDeviceCapability.LightState, device.Capabilities);
            Assert.Contains(devices, original => ReferenceEquals(original, device));
        });
    }

    /// <summary>
    /// Verifies that availability is counted from compatible active light devices
    /// rather than from every catalog entry or repeated capability.
    /// </summary>
    [Fact]
    public void ResolveRandomLightWithInsufficientCompatibleDevicesThrows()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 4);
        Assert.Equal(10, devices.Length);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, devices, new Random(42)));

        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies that fresh random sources with the same seed select the same
    /// light devices independently of the incoming catalog order.
    /// </summary>
    /// <param name="seed">The seed shared by independent random sources.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-17)]
    public void ResolveRandomLightWithSameSeedIgnoresInputOrder(int seed)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 2);

        var first = resolver.Resolve(scenario, devices, new Random(seed));
        var repeated = resolver.Resolve(scenario, devices, new Random(seed));
        var reversed = resolver.Resolve(scenario, devices.Reverse().ToArray(), new Random(seed));
        var rotated = resolver.Resolve(scenario,
            devices.Skip(2).Concat(devices.Take(2)).ToArray(), new Random(seed));

        var expectedIds = first.Select(device => device.Id).ToArray();
        Assert.Equal(expectedIds, repeated.Select(device => device.Id));
        Assert.Equal(expectedIds, reversed.Select(device => device.Id));
        Assert.Equal(expectedIds, rotated.Select(device => device.Id));
    }

    /// <summary>
    /// Verifies that deterministic light targets preserve the shared random state
    /// used by later scenario resolutions.
    /// </summary>
    /// <param name="mode">The deterministic target mode.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All)]
    [InlineData(ScenarioTargetMode.SpecificDevice)]
    public void ResolveNonrandomLightTargetDoesNotAdvanceRandomState(ScenarioTargetMode mode)
    {
        var resolver = new ScenarioTargetResolver();
        var random = new Random(42);
        var untouchedRandom = new Random(42);

        resolver.Resolve(CreateScenarioForMode(mode), CreateDevices(), random);

        for (var index = 0; index < 8; index++)
        {
            Assert.Equal(untouchedRandom.Next(), random.Next());
        }
    }

    /// <summary>
    /// Verifies that the plan resolver preserves definition order while resolving
    /// light, temperature, and humidity targets by their own capabilities.
    /// A multi-capability device is eligible for all three scenarios.
    /// </summary>
    [Fact]
    public void ResolveMixedPlanUsesEachScenarioCapabilityAndPreservesDefinitionOrder()
    {
        var devices = CreateDevices();
        var light = CreateScenario();
        var temperature = new HighTemperatureScenarioDefinition(
            "GradualHighTemperature", TimeSpan.Zero, TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true, TimeSpan.FromMinutes(2), 30, 35, 2, 2);
        var humidity = new HighHumidityScenarioDefinition(
            "GradualHighHumidity", TimeSpan.Zero, TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true, TimeSpan.FromMinutes(2), 90, 95, 3, 3);
        var plan = new SimulationPlanDefinition("MixedDoorPlan", 42,
            new ScenarioDefinition[] { light, temperature, humidity });
        var resolver = new SimulationPlanResolver(new ScenarioTargetResolver());

        var resolved = resolver.Resolve(plan, devices);

        Assert.Equal(3, resolved.Count);
        Assert.Same(light, resolved[0].Definition);
        Assert.Same(temperature, resolved[1].Definition);
        Assert.Same(humidity, resolved[2].Definition);
        Assert.Equal(new[] { "LT-002", "LT-010", "lt-003" },
            resolved[0].Devices.Select(device => device.Code));
        Assert.Equal(new[] { "LT-004", "LT-010" },
            resolved[1].Devices.Select(device => device.Code));
        Assert.Equal(new[] { "LT-004", "LT-010", "LT-011" },
            resolved[2].Devices.Select(device => device.Code));
        Assert.Same(devices[0], resolved[0].Devices[1]);
        Assert.Same(devices[0], resolved[1].Devices[1]);
        Assert.Same(devices[0], resolved[2].Devices[1]);
    }

    /// <summary>Creates a light definition with configurable target settings.</summary>
    /// <param name="mode">The target mode.</param>
    /// <param name="count">The optional random-selection count.</param>
    /// <param name="deviceCode">The optional specific-device code.</param>
    /// <returns>A definition with immediate automatic recovery.</returns>
    private static LightOnScenarioDefinition CreateScenario(
        ScenarioTargetMode mode = ScenarioTargetMode.All,
        int? count = null, string? deviceCode = null) => new(
            "ScheduledLightOn", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(mode, count, deviceCode), true, null);

    /// <summary>Creates valid target settings for each supported target mode.</summary>
    /// <param name="mode">The supported target mode.</param>
    /// <returns>A scenario targeting all candidates, LT-002, or two random candidates.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the fixture mode is unsupported.
    /// </exception>
    private static LightOnScenarioDefinition CreateScenarioForMode(ScenarioTargetMode mode) =>
        mode switch
        {
            ScenarioTargetMode.All => CreateScenario(mode),
            ScenarioTargetMode.SpecificDevice => CreateScenario(mode, deviceCode: "LT-002"),
            ScenarioTargetMode.RandomCompatible => CreateScenario(mode, count: 2),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported fixture mode.")
        };

    /// <summary>
    /// Creates an unsorted catalog whose first three devices are active light
    /// devices. Other entries cover unrelated capabilities and inactive lifecycles.
    /// </summary>
    /// <returns>Ten device objects owned by the fixture.</returns>
    private static SimulatorDeviceResponse[] CreateDevices() => new[]
    {
        CreateDevice(1, "LT-010", capabilities: new[]
        {
            SimulatorDeviceCapability.LightState,
            SimulatorDeviceCapability.Temperature,
            SimulatorDeviceCapability.Humidity,
            SimulatorDeviceCapability.DoorState
        }),
        CreateDevice(2, "LT-002"),
        CreateDevice(3, "lt-003", capabilities: new[]
        {
            SimulatorDeviceCapability.LightState, SimulatorDeviceCapability.LightState
        }),
        CreateDevice(4, "LT-004", capabilities: new[]
        {
            SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity
        }),
        CreateDevice(5, "LT-005", SimulatorDeviceLifecycle.Registered),
        CreateDevice(6, "LT-006", SimulatorDeviceLifecycle.Retired),
        CreateDevice(7, "LT-007", capabilities: new[] { SimulatorDeviceCapability.DoorState }),
        CreateDevice(8, "LT-008", capabilities: Array.Empty<SimulatorDeviceCapability>()),
        CreateDevice(9, "LT-009", (SimulatorDeviceLifecycle)999),
        CreateDevice(10, "LT-011", capabilities: new[] { SimulatorDeviceCapability.Humidity })
    };

    /// <summary>Creates a deterministic device with configurable compatibility.</summary>
    /// <param name="number">The unique numeric suffix of the identifier.</param>
    /// <param name="code">The device code.</param>
    /// <param name="lifecycle">The lifecycle, active by default.</param>
    /// <param name="capabilities">The capabilities, or null to use light-state capability.</param>
    /// <returns>A device belonging to the test fixture.</returns>
    private static SimulatorDeviceResponse CreateDevice(
        int number, string code,
        SimulatorDeviceLifecycle lifecycle = SimulatorDeviceLifecycle.Active,
        IReadOnlyList<SimulatorDeviceCapability>? capabilities = null) => new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), code, $"Light Device {number}",
            capabilities ?? new[] { SimulatorDeviceCapability.LightState }, lifecycle);
}
