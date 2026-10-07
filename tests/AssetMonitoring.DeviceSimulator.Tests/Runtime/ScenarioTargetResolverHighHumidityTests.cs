using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies humidity compatibility, all target modes, deterministic selection,
/// and scenario-type dispatch through the public target resolver.
/// </summary>
public sealed class ScenarioTargetResolverHighHumidityTests
{
    /// <summary>
    /// Verifies that All selects only active humidity devices in ordinal code order,
    /// including humidity-only devices and devices with additional capabilities.
    /// </summary>
    [Fact]
    public void ResolveAllForHighHumidityReturnsOnlyActiveHumidityDevicesInOrdinalOrder()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();

        var selected = resolver.Resolve(CreateScenario(), devices, new Random(42));

        Assert.Equal(new[] { "WH-002", "WH-010", "wh-003" }, selected.Select(device => device.Code));
        Assert.Equal(3, selected.Count);
        Assert.Same(devices[1], selected[0]);
        Assert.Same(devices[0], selected[1]);
        Assert.Same(devices[2], selected[2]);
    }

    /// <summary>Verifies exact-code targeting returns the original eligible humidity device.</summary>
    /// <param name="deviceCode">The exact code of a compatible device.</param>
    /// <param name="index">The device's index in the deliberately unsorted fixture.</param>
    [Theory]
    [InlineData("WH-010", 0)]
    [InlineData("WH-002", 1)]
    [InlineData("wh-003", 2)]
    public void ResolveSpecificHumidityDeviceReturnsRequestedOriginalDevice(string deviceCode, int index)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var selected = resolver.Resolve(scenario, devices, new Random(42));

        Assert.Same(devices[index], Assert.Single(selected));
    }

    /// <summary>
    /// Verifies that a specific humidity target must be active and humidity-capable,
    /// even when other compatible devices are available.
    /// </summary>
    /// <param name="deviceCode">The existing but incompatible target code.</param>
    [Theory]
    [InlineData("WH-004")]
    [InlineData("WH-005")]
    [InlineData("WH-006")]
    [InlineData("WH-007")]
    [InlineData("WH-008")]
    [InlineData("WH-009")]
    [InlineData("WH-011")]
    public void ResolveSpecificHumidityDeviceWithIncompatibleTargetThrows(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>Verifies that specific humidity target codes are matched exactly.</summary>
    /// <param name="deviceCode">The absent or inexact target code.</param>
    [Theory]
    [InlineData("WH-999")]
    [InlineData("wh-002")]
    [InlineData(" WH-002 ")]
    public void ResolveSpecificHumidityDeviceWithoutExactCodeMatchThrows(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies every target mode fails when the catalog is empty or contains
    /// no active humidity device, including a catalog with active temperature devices.
    /// </summary>
    /// <param name="mode">The target mode under test.</param>
    /// <param name="emptyCatalog">Whether to supply an empty catalog.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All, true)]
    [InlineData(ScenarioTargetMode.All, false)]
    [InlineData(ScenarioTargetMode.SpecificDevice, true)]
    [InlineData(ScenarioTargetMode.SpecificDevice, false)]
    [InlineData(ScenarioTargetMode.RandomCompatible, true)]
    [InlineData(ScenarioTargetMode.RandomCompatible, false)]
    public void ResolveHighHumidityWithoutCompatibleDevicesThrows(
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
    /// Verifies that random humidity selection returns the requested number of
    /// distinct eligible devices from the supplied catalog.
    /// </summary>
    /// <param name="count">The number of compatible devices to select.</param>
    /// <param name="seed">The random seed.</param>
    [Theory]
    [InlineData(1, 42)]
    [InlineData(2, 42)]
    [InlineData(3, 42)]
    [InlineData(2, 0)]
    [InlineData(2, -17)]
    public void ResolveRandomHumidityReturnsRequestedDistinctEligibleDevices(int count, int seed)
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
            Assert.Contains(SimulatorDeviceCapability.Humidity, device.Capabilities);
            Assert.Contains(devices, original => ReferenceEquals(original, device));
        });
    }

    /// <summary>Verifies the requested random count is checked against humidity-compatible devices.</summary>
    [Fact]
    public void ResolveRandomHumidityWithInsufficientCompatibleDevicesThrows()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 4);
        Assert.Equal(10, devices.Length);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, devices, new Random(42)));

        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>Verifies random targeting succeeds with one humidity-only candidate.</summary>
    [Fact]
    public void ResolveRandomHumidityWithOneEligibleDeviceReturnsThatDevice()
    {
        var resolver = new ScenarioTargetResolver();
        var eligible = CreateDevice(1, "WH-001");
        var devices = new[]
        {
            CreateDevice(2, "WH-002", capabilities: new[] { SimulatorDeviceCapability.Temperature }),
            eligible
        };

        var selected = resolver.Resolve(
            CreateScenario(ScenarioTargetMode.RandomCompatible, count: 1), devices, new Random(42));

        Assert.Same(eligible, Assert.Single(selected));
    }

    /// <summary>
    /// Verifies fresh random sources with the same seed produce the same humidity
    /// selection regardless of the input device order.
    /// </summary>
    /// <param name="seed">The seed shared by independent random sources.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-17)]
    public void ResolveRandomHumidityWithSameSeedIgnoresInputOrder(int seed)
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

    /// <summary>Verifies deterministic humidity target modes do not consume the supplied random state.</summary>
    /// <param name="mode">The deterministic target mode.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All)]
    [InlineData(ScenarioTargetMode.SpecificDevice)]
    public void ResolveNonrandomHumidityTargetDoesNotAdvanceRandomState(ScenarioTargetMode mode)
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

    /// <summary>Verifies every humidity target mode preserves the original device data and input order.</summary>
    /// <param name="mode">The target mode under test.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All)]
    [InlineData(ScenarioTargetMode.SpecificDevice)]
    [InlineData(ScenarioTargetMode.RandomCompatible)]
    public void ResolveHumidityDoesNotModifyInputDevices(ScenarioTargetMode mode)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices().ToList();
        var originalDevices = devices.ToArray();
        var originalCapabilities = devices.Select(device => device.Capabilities.ToArray()).ToArray();

        resolver.Resolve(CreateScenarioForMode(mode), devices, new Random(42));

        Assert.Equal(originalDevices.Length, devices.Count);
        for (var index = 0; index < originalDevices.Length; index++)
        {
            Assert.Same(originalDevices[index], devices[index]);
            Assert.Equal(originalCapabilities[index], devices[index].Capabilities);
        }
    }

    /// <summary>
    /// Verifies scenario types select different capabilities from the same
    /// catalog while a device with both capabilities is eligible for both.
    /// </summary>
    [Fact]
    public void ResolveDifferentScenarioTypesUseTheirOwnRequiredCapabilities()
    {
        var resolver = new ScenarioTargetResolver();
        var humidityOnly = CreateDevice(1, "WH-001");
        var temperatureOnly = CreateDevice(2, "WH-002",
            capabilities: new[] { SimulatorDeviceCapability.Temperature });
        var both = CreateDevice(3, "WH-003",
            capabilities: new[] { SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity });
        var devices = new[] { both, temperatureOnly, humidityOnly };
        var temperatureScenario = new HighTemperatureScenarioDefinition(
            "Temperature", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(2),
            40, 45, 3, 3);

        var humiditySelection = resolver.Resolve(CreateScenario(), devices, new Random(42));
        var temperatureSelection = resolver.Resolve(temperatureScenario, devices, new Random(42));

        Assert.Equal(new[] { humidityOnly.Id, both.Id }, humiditySelection.Select(device => device.Id));
        Assert.Equal(new[] { temperatureOnly.Id, both.Id }, temperatureSelection.Select(device => device.Id));
        Assert.Same(both, humiditySelection[1]);
        Assert.Same(both, temperatureSelection[1]);
    }

    /// <summary>
    /// Verifies an unknown scenario type is rejected before device filtering,
    /// including when an empty catalog could otherwise produce an availability error.
    /// </summary>
    [Fact]
    public void ResolveUnknownScenarioWithEmptyCatalogThrowsNotSupportedException()
    {
        var resolver = new ScenarioTargetResolver();

        var exception = Assert.Throws<NotSupportedException>(() => resolver.Resolve(
            new UnsupportedScenarioDefinition(), Array.Empty<SimulatorDeviceResponse>(), new Random(42)));

        Assert.Contains(nameof(UnsupportedScenarioDefinition), exception.Message);
    }

    /// <summary>Creates a humidity definition with configurable target settings.</summary>
    /// <param name="mode">The target mode.</param>
    /// <param name="count">The optional random-selection count.</param>
    /// <param name="deviceCode">The optional specific-device code.</param>
    /// <returns>A humidity definition with valid timing and numeric settings.</returns>
    private static HighHumidityScenarioDefinition CreateScenario(
        ScenarioTargetMode mode = ScenarioTargetMode.All,
        int? count = null,
        string? deviceCode = null) => new(
            "GradualHighHumidity", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(mode, count, deviceCode), true, TimeSpan.FromMinutes(2),
            90, 95, 3, 3);

    /// <summary>Creates valid target settings for each supported target mode.</summary>
    /// <param name="mode">The supported target mode.</param>
    /// <returns>A scenario targeting all devices, WH-002, or two random devices.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the fixture mode is unsupported.</exception>
    private static HighHumidityScenarioDefinition CreateScenarioForMode(ScenarioTargetMode mode) =>
        mode switch
        {
            ScenarioTargetMode.All => CreateScenario(mode),
            ScenarioTargetMode.SpecificDevice => CreateScenario(mode, deviceCode: "WH-002"),
            ScenarioTargetMode.RandomCompatible => CreateScenario(mode, count: 2),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported fixture mode.")
        };

    /// <summary>
    /// Creates an unsorted catalog where only the first three devices are active
    /// humidity devices. Other entries cover incompatible capabilities and lifecycles.
    /// </summary>
    /// <returns>Ten original device objects used by the resolver tests.</returns>
    private static SimulatorDeviceResponse[] CreateDevices() => new[]
    {
        CreateDevice(1, "WH-010", capabilities: new[]
        {
            SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity
        }),
        CreateDevice(2, "WH-002"),
        CreateDevice(3, "wh-003", capabilities: new[]
        {
            SimulatorDeviceCapability.Humidity, SimulatorDeviceCapability.Humidity
        }),
        CreateDevice(4, "WH-004", capabilities: new[] { SimulatorDeviceCapability.Temperature }),
        CreateDevice(5, "WH-005", SimulatorDeviceLifecycle.Registered),
        CreateDevice(6, "WH-006", SimulatorDeviceLifecycle.Retired),
        CreateDevice(7, "WH-007", capabilities: new[] { SimulatorDeviceCapability.DoorState }),
        CreateDevice(8, "WH-008", capabilities: new[] { SimulatorDeviceCapability.LightState }),
        CreateDevice(9, "WH-009", capabilities: Array.Empty<SimulatorDeviceCapability>()),
        CreateDevice(10, "WH-011", (SimulatorDeviceLifecycle)999)
    };

    /// <summary>Creates a deterministic device with configurable compatibility.</summary>
    /// <param name="number">The unique numeric suffix of the device identifier.</param>
    /// <param name="code">The device code.</param>
    /// <param name="lifecycle">The lifecycle, active by default.</param>
    /// <param name="capabilities">The capabilities, or null to use humidity capability.</param>
    /// <returns>A device owned by the test fixture.</returns>
    private static SimulatorDeviceResponse CreateDevice(
        int number, string code,
        SimulatorDeviceLifecycle lifecycle = SimulatorDeviceLifecycle.Active,
        IReadOnlyList<SimulatorDeviceCapability>? capabilities = null) => new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), code, $"Warehouse Device {number}",
            capabilities ?? new[] { SimulatorDeviceCapability.Humidity }, lifecycle);

    /// <summary>Represents an unknown type for testing the resolver's supported-type guard.</summary>
    private sealed record UnsupportedScenarioDefinition() : ScenarioDefinition(
        "UnsupportedScenario", TimeSpan.Zero, TimeSpan.FromMinutes(1),
        new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(1));
}
