using AssetMonitoring.DeviceSimulator.Api.Contracts;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Runtime;

/// <summary>
/// Verifies scenario compatibility, all target modes, validation failures,
/// reproducible random selection, and preservation of the supplied devices.
/// </summary>
/// <remarks>
/// These synchronous tests require no HTTP client, clock, or additional package.
/// Random tests verify counts, membership, uniqueness, and repeatability without
/// requiring a hard-coded sequence for a seed or different results for different seeds.
/// Scenario targets and device capability collections are assumed to be nonnull,
/// as required by the resolver's documented input contract.
/// </remarks>
public sealed class ScenarioTargetResolverTests
{
    /// <summary>Verifies that all operation arguments are required.</summary>
    /// <param name="argument">The argument to omit.</param>
    [Theory]
    [InlineData("scenario")]
    [InlineData("devices")]
    [InlineData("random")]
    public void ResolveWithNullArgumentThrowsArgumentNullException(string argument)
    {
        var resolver = new ScenarioTargetResolver();

        var exception = Assert.Throws<ArgumentNullException>(() => resolver.Resolve(
            argument == "scenario" ? null! : CreateScenario(),
            argument == "devices" ? null! : CreateDevices(),
            argument == "random" ? null! : new Random(42)));

        Assert.Equal(argument, exception.ParamName);
    }

    /// <summary>Verifies that unsupported scenario types are rejected explicitly.</summary>
    [Fact]
    public void ResolveWithUnsupportedScenarioThrowsNotSupportedException()
    {
        var resolver = new ScenarioTargetResolver();

        var exception = Assert.Throws<NotSupportedException>(() => resolver.Resolve(
            new UnsupportedScenarioDefinition(), CreateDevices(), new Random(42)));

        Assert.Contains(nameof(UnsupportedScenarioDefinition), exception.Message);
    }

    /// <summary>Verifies that undefined target modes are rejected explicitly.</summary>
    /// <param name="mode">The undefined enum value.</param>
    [Theory]
    [InlineData((ScenarioTargetMode)(-1))]
    [InlineData((ScenarioTargetMode)999)]
    public void ResolveWithUnsupportedTargetModeThrowsNotSupportedException(ScenarioTargetMode mode)
    {
        var resolver = new ScenarioTargetResolver();

        var exception = Assert.Throws<NotSupportedException>(() => resolver.Resolve(
            CreateScenario(mode), CreateDevices(), new Random(42)));

        Assert.Contains(mode.ToString(), exception.Message);
    }

    /// <summary>
    /// Verifies active-temperature compatibility and ordinal code ordering.
    /// Devices with extra or repeated temperature capabilities remain eligible;
    /// other metrics, inactive devices, and empty capabilities are excluded.
    /// </summary>
    [Fact]
    public void ResolveAllReturnsOnlyCompatibleDevicesInOrdinalCodeOrder()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();

        var selected = resolver.Resolve(CreateScenario(), devices, new Random(42));

        Assert.Equal(new[] { "WH-002", "WH-010", "wh-003" }, selected.Select(x => x.Code));
        Assert.Equal(3, selected.Count);
        Assert.Same(devices[1], selected[0]);
        Assert.Same(devices[0], selected[1]);
        Assert.Same(devices[2], selected[2]);
    }

    /// <summary>
    /// Verifies that every target mode fails when no compatible devices exist,
    /// both for an empty catalog and for a catalog containing only ineligible devices.
    /// </summary>
    /// <param name="mode">The target mode under test.</param>
    /// <param name="emptyCatalog">Whether the entire input collection is empty.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All, true)]
    [InlineData(ScenarioTargetMode.All, false)]
    [InlineData(ScenarioTargetMode.SpecificDevice, true)]
    [InlineData(ScenarioTargetMode.SpecificDevice, false)]
    [InlineData(ScenarioTargetMode.RandomCompatible, true)]
    [InlineData(ScenarioTargetMode.RandomCompatible, false)]
    public void ResolveWithoutCompatibleDevicesThrowsInvalidOperationException(
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

    /// <summary>Verifies exact-code selection returns one original compatible device.</summary>
    [Fact]
    public void ResolveSpecificDeviceReturnsOnlyTheRequestedDevice()
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: "WH-010");

        var selected = resolver.Resolve(scenario, devices, new Random(42));

        Assert.Same(devices[0], Assert.Single(selected));
    }

    /// <summary>Verifies that a specific target requires a nonblank code.</summary>
    /// <param name="deviceCode">The missing or blank target code.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void ResolveSpecificDeviceWithMissingCodeThrowsArgumentException(string? deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<ArgumentException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Equal("scenario", exception.ParamName);
    }

    /// <summary>
    /// Verifies that code lookup is exact, including case and surrounding whitespace,
    /// and a missing code produces an error identifying the requested target.
    /// </summary>
    /// <param name="deviceCode">The code that does not exactly match an available device.</param>
    [Theory]
    [InlineData("WH-999")]
    [InlineData("wh-002")]
    [InlineData(" WH-002 ")]
    public void ResolveSpecificDeviceWithoutExactCodeMatchThrowsInvalidOperationException(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies that an existing but incompatible target is rejected even when
    /// other compatible devices could have been selected instead.
    /// </summary>
    /// <param name="deviceCode">The registered, retired, humidity-only, or empty-capability device.</param>
    [Theory]
    [InlineData("WH-004")]
    [InlineData("WH-005")]
    [InlineData("WH-006")]
    [InlineData("WH-009")]
    public void ResolveSpecificDeviceWithIncompatibleTargetThrowsInvalidOperationException(string deviceCode)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.SpecificDevice, deviceCode: deviceCode);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Contains(deviceCode, exception.Message);
        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>Verifies that random targeting requires a positive count.</summary>
    /// <param name="count">The missing or nonpositive count.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveRandomCompatibleWithInvalidCountThrowsArgumentException(int? count)
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count);

        var exception = Assert.Throws<ArgumentException>(() =>
            resolver.Resolve(scenario, CreateDevices(), new Random(42)));

        Assert.Equal("scenario", exception.ParamName);
    }

    /// <summary>
    /// Verifies that the count is checked against compatible devices,
    /// rather than against the larger unfiltered input collection.
    /// </summary>
    [Fact]
    public void ResolveRandomCompatibleWithInsufficientCompatibleDevicesThrowsInvalidOperationException()
    {
        var resolver = new ScenarioTargetResolver();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 4);
        var devices = CreateDevices();

        // This catalog has ten devices, but only three are compatible.
        Assert.Equal(10, devices.Length);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(scenario, devices, new Random(42)));

        Assert.Contains(scenario.Name, exception.Message);
    }

    /// <summary>
    /// Verifies the exact requested count, eligibility, and selection without
    /// duplicate devices, including a request for every compatible device.
    /// </summary>
    /// <param name="count">The number of compatible devices to select.</param>
    /// <param name="seed">The seed used for this selection.</param>
    [Theory]
    [InlineData(1, 42)]
    [InlineData(2, 42)]
    [InlineData(3, 42)]
    [InlineData(2, 0)]
    [InlineData(2, -17)]
    public void ResolveRandomCompatibleReturnsRequestedDistinctEligibleDevices(int count, int seed)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count);
        // The first three fixture devices are explicitly eligible.
        var eligibleIds = devices.Take(3).Select(x => x.Id).ToHashSet();

        var selected = resolver.Resolve(scenario, devices, new Random(seed));

        Assert.Equal(count, selected.Count);
        Assert.Equal(count, selected.Select(x => x.Id).Distinct().Count());
        Assert.All(selected, device =>
        {
            Assert.Contains(device.Id, eligibleIds);
            Assert.Contains(devices, original => ReferenceEquals(original, device));
        });
    }

    /// <summary>
    /// Verifies that the only eligible device can be selected successfully,
    /// covering the zero index in a one-element candidate collection.
    /// </summary>
    [Fact]
    public void ResolveRandomCompatibleWithOneEligibleDeviceReturnsThatDevice()
    {
        var resolver = new ScenarioTargetResolver();
        var eligible = CreateDevice(1, "WH-001");
        var devices = new[]
        {
            CreateDevice(2, "WH-002", capabilities: new[] { SimulatorDeviceCapability.Humidity }),
            eligible
        };
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 1);

        var selected = resolver.Resolve(scenario, devices, new Random(42));

        Assert.Same(eligible, Assert.Single(selected));
    }

    /// <summary>
    /// Verifies reproducibility from fresh random sources with the same seed,
    /// regardless of the order in which the API supplied compatible devices.
    /// </summary>
    /// <param name="seed">The seed shared by the independent random sources.</param>
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-17)]
    public void ResolveRandomCompatibleWithSameSeedIgnoresInputOrder(int seed)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices();
        var reversed = devices.Reverse().ToArray();
        var rotated = devices.Skip(2).Concat(devices.Take(2)).ToArray();
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 2);

        var first = resolver.Resolve(scenario, devices, new Random(seed));
        var repeated = resolver.Resolve(scenario, devices, new Random(seed));
        var fromReversed = resolver.Resolve(scenario, reversed, new Random(seed));
        var fromRotated = resolver.Resolve(scenario, rotated, new Random(seed));

        var expectedIds = first.Select(x => x.Id).ToArray();
        Assert.Equal(expectedIds, repeated.Select(x => x.Id));
        Assert.Equal(expectedIds, fromReversed.Select(x => x.Id));
        Assert.Equal(expectedIds, fromRotated.Select(x => x.Id));
    }

    /// <summary>
    /// Verifies that successive random resolutions use the caller-supplied
    /// random object instead of creating a private random source.
    /// </summary>
    [Fact]
    public void ResolveRandomCompatibleUsesSuppliedRandomAcrossResolutions()
    {
        var resolver = new ScenarioTargetResolver();
        var random = new RecordingRandom(42);
        var scenario = CreateScenario(ScenarioTargetMode.RandomCompatible, count: 2);
        var devices = CreateDevices();

        resolver.Resolve(scenario, devices, random);
        var firstCallCount = random.CallCount;
        Assert.True(firstCallCount > 0);

        resolver.Resolve(scenario, devices, random);

        Assert.True(random.CallCount > firstCallCount);
    }

    /// <summary>
    /// Verifies that deterministic target modes preserve the plan's random state
    /// for scenarios that actually require random selection.
    /// </summary>
    /// <param name="mode">The deterministic target mode.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All)]
    [InlineData(ScenarioTargetMode.SpecificDevice)]
    public void ResolveNonrandomTargetDoesNotAdvanceRandomState(ScenarioTargetMode mode)
    {
        var resolver = new ScenarioTargetResolver();
        var random = new Random(42);
        var untouchedRandom = new Random(42);

        resolver.Resolve(CreateScenarioForMode(mode), CreateDevices(), random);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(untouchedRandom.Next(), random.Next());
        }
    }

    /// <summary>
    /// Verifies that selection preserves input ordering, device identities,
    /// and capability values for every target mode.
    /// </summary>
    /// <param name="mode">The target mode under test.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All)]
    [InlineData(ScenarioTargetMode.SpecificDevice)]
    [InlineData(ScenarioTargetMode.RandomCompatible)]
    public void ResolveDoesNotModifyInputDevices(ScenarioTargetMode mode)
    {
        var resolver = new ScenarioTargetResolver();
        var devices = CreateDevices().ToList();
        var originalDevices = devices.ToArray();
        var originalCapabilities = devices.Select(x => x.Capabilities.ToArray()).ToArray();

        resolver.Resolve(CreateScenarioForMode(mode), devices, new Random(42));

        Assert.Equal(originalDevices.Length, devices.Count);
        for (var index = 0; index < originalDevices.Length; index++)
        {
            Assert.Same(originalDevices[index], devices[index]);
            Assert.Equal(originalCapabilities[index], devices[index].Capabilities);
        }
    }

    /// <summary>Creates a high-temperature scenario with the specified targeting values.</summary>
    /// <param name="mode">The target mode.</param>
    /// <param name="count">The optional number of devices for random targeting.</param>
    /// <param name="deviceCode">The optional specific device code.</param>
    /// <returns>A scenario with valid timing and temperature settings.</returns>
    private static HighTemperatureScenarioDefinition CreateScenario(
        ScenarioTargetMode mode = ScenarioTargetMode.All,
        int? count = null,
        string? deviceCode = null) =>
        new(
            "GradualHighTemperature",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(mode, count, deviceCode),
            true,
            TimeSpan.FromMinutes(2),
            30,
            35,
            2,
            2);

    /// <summary>Creates a scenario with valid target fields for a known mode.</summary>
    /// <param name="mode">The supported target mode.</param>
    /// <returns>A scenario targeting all devices, two random devices, or WH-002.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the mode is unsupported.</exception>
    private static HighTemperatureScenarioDefinition CreateScenarioForMode(ScenarioTargetMode mode) =>
        mode switch
        {
            ScenarioTargetMode.All => CreateScenario(mode),
            ScenarioTargetMode.RandomCompatible => CreateScenario(mode, count: 2),
            ScenarioTargetMode.SpecificDevice => CreateScenario(mode, deviceCode: "WH-002"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported fixture mode.")
        };

    /// <summary>
    /// Creates ten devices in deliberately unsorted order. Only the first three
    /// are active temperature devices; the remaining seven are incompatible.
    /// </summary>
    /// <returns>The mixed device catalog used by resolver tests.</returns>
    private static SimulatorDeviceResponse[] CreateDevices() =>
        new[]
        {
            CreateDevice(1, "WH-010", capabilities: new[]
            {
                SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Humidity
            }),
            CreateDevice(2, "WH-002"),
            CreateDevice(3, "wh-003", capabilities: new[]
            {
                SimulatorDeviceCapability.Temperature, SimulatorDeviceCapability.Temperature
            }),
            CreateDevice(4, "WH-004", SimulatorDeviceLifecycle.Registered),
            CreateDevice(5, "WH-005", SimulatorDeviceLifecycle.Retired),
            CreateDevice(6, "WH-006", capabilities: new[] { SimulatorDeviceCapability.Humidity }),
            CreateDevice(7, "WH-007", capabilities: new[] { SimulatorDeviceCapability.DoorState }),
            CreateDevice(8, "WH-008", capabilities: new[] { SimulatorDeviceCapability.LightState }),
            CreateDevice(9, "WH-009", capabilities: Array.Empty<SimulatorDeviceCapability>()),
            CreateDevice(10, "WH-011", (SimulatorDeviceLifecycle)999)
        };

    /// <summary>Creates a device with deterministic identity and configurable compatibility.</summary>
    /// <param name="number">The numeric suffix of the device identifier.</param>
    /// <param name="code">The device catalog code.</param>
    /// <param name="lifecycle">The device lifecycle, active by default.</param>
    /// <param name="capabilities">The capabilities, or null to use temperature capability.</param>
    /// <returns>A device for a resolver test.</returns>
    private static SimulatorDeviceResponse CreateDevice(
        int number,
        string code,
        SimulatorDeviceLifecycle lifecycle = SimulatorDeviceLifecycle.Active,
        IReadOnlyList<SimulatorDeviceCapability>? capabilities = null) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
            code,
            $"Warehouse Device {number}",
            capabilities ?? new[] { SimulatorDeviceCapability.Temperature },
            lifecycle);

    /// <summary>Provides an unrecognized scenario type for the supported-type guard.</summary>
    private sealed record UnsupportedScenarioDefinition() : ScenarioDefinition(
        "UnsupportedScenario",
        TimeSpan.Zero,
        TimeSpan.FromMinutes(1),
        new ScenarioTargetDefinition(ScenarioTargetMode.All),
        false,
        null);

    /// <summary>
    /// Records use of the supplied random source while preserving normal seeded behavior.
    /// Assertions deliberately do not depend on an exact number of random draws.
    /// </summary>
    private sealed class RecordingRandom : Random
    {
        /// <summary>Creates a seeded random source with usage tracking.</summary>
        /// <param name="seed">The initial random seed.</param>
        public RecordingRandom(int seed) : base(seed)
        {
        }

        /// <summary>Gets the number of observed random method calls.</summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        public override int Next()
        {
            CallCount++;
            return base.Next();
        }

        /// <inheritdoc />
        public override int Next(int maxValue)
        {
            CallCount++;
            return base.Next(maxValue);
        }

        /// <inheritdoc />
        public override int Next(int minValue, int maxValue)
        {
            CallCount++;
            return base.Next(minValue, maxValue);
        }

        /// <inheritdoc />
        public override double NextDouble()
        {
            CallCount++;
            return base.NextDouble();
        }
    }
}
