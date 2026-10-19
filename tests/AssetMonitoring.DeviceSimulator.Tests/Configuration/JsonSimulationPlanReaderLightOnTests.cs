using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Nodes;

namespace AssetMonitoring.DeviceSimulator.Tests.Configuration;

/// <summary>
/// Verifies light-on definitions through the production JSON reader
/// and their immediate-recovery validation contract.
/// </summary>
public sealed class JsonSimulationPlanReaderLightOnTests
{
    private const string DoorPlanJson = """
        {
          "name": "WarehouseLightOn",
          "seed": 42,
          "scenarios": [
            {
              "type": "LightOn",
              "name": "ScheduledLightOn",
              "startsAfter": "00:01:00",
              "duration": "00:03:00",
              "target": { "mode": "SpecificDevice", "deviceCode": "LIGHT-001" },
              "autoRecover": true,
              "recoveryDuration": null
            }
          ]
        }
        """;

    /// <summary>
    /// Verifies the LightOn discriminator and every configured property
    /// through the production reader and public validator.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithLightOnDeserializesEveryConfiguredProperty()
    {
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(DoorPlanJson);

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        Assert.Equal("WarehouseLightOn", plan.Name);
        Assert.Equal(42, plan.Seed);
        var scenario = Assert.IsType<LightOnScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.Equal("ScheduledLightOn", scenario.Name);
        Assert.Equal(TimeSpan.FromMinutes(1), scenario.StartsAfter);
        Assert.Equal(TimeSpan.FromMinutes(3), scenario.Duration);
        Assert.Equal(ScenarioTargetMode.SpecificDevice, scenario.Target.Mode);
        Assert.Equal("LIGHT-001", scenario.Target.DeviceCode);
        Assert.Null(scenario.Target.Count);
        Assert.True(scenario.AutoRecover);
        Assert.Null(scenario.RecoveryDuration);
        AssertValid(plan);
    }

    /// <summary>
    /// Verifies that omitting the optional recovery duration produces the same
    /// valid immediate-recovery configuration as an explicit JSON null.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithLightOnAndMissingRecoveryDurationUsesNullAndValidates()
    {
        var json = JsonNode.Parse(DoorPlanJson)!;
        Assert.True(json["scenarios"]![0]!.AsObject().Remove("recoveryDuration"));
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        var scenario = Assert.IsType<LightOnScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.True(scenario.AutoRecover);
        Assert.Null(scenario.RecoveryDuration);
        AssertValid(plan);
    }

    /// <summary>
    /// Verifies the remaining target modes through JSON deserialization
    /// and light-specific plan validation.
    /// </summary>
    /// <param name="mode">The target-mode string supplied in JSON.</param>
    /// <param name="count">The optional number of randomly selected devices.</param>
    [Theory]
    [InlineData("All", null)]
    [InlineData("RandomCompatible", 2)]
    public async Task ReadAsyncWithLightOnPreservesTargetModeAndValidates(string mode, int? count)
    {
        var json = JsonNode.Parse(DoorPlanJson)!;
        json["scenarios"]![0]!["target"] = new JsonObject
        {
            ["mode"] = mode,
            ["count"] = count
        };
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        var scenario = Assert.IsType<LightOnScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.Equal(Enum.Parse<ScenarioTargetMode>(mode), scenario.Target.Mode);
        Assert.Equal(count, scenario.Target.Count);
        Assert.Null(scenario.Target.DeviceCode);
        AssertValid(plan);
    }

    /// <summary>
    /// Verifies that the reader preserves disabled automatic recovery and
    /// leaves rejection of that unsupported setting to the validator.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithLightOnWithoutAutomaticRecoveryPreservesValueAndValidationRejectsIt()
    {
        var json = JsonNode.Parse(DoorPlanJson)!;
        json["scenarios"]![0]!["autoRecover"] = false;
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        var scenario = Assert.IsType<LightOnScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.False(scenario.AutoRecover);
        Assert.Null(scenario.RecoveryDuration);
        AssertSingleValidationError(plan,
            "Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover");
    }

    /// <summary>
    /// Verifies that duration values are read successfully but rejected by
    /// validation because light recovery is immediate.
    /// </summary>
    /// <param name="duration">The TimeSpan string supplied in JSON.</param>
    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:00")]
    [InlineData("00:00:01")]
    public async Task ReadAsyncWithLightOnAndSuppliedRecoveryDurationPreservesValueAndValidationRejectsIt(
        string duration)
    {
        var json = JsonNode.Parse(DoorPlanJson)!;
        json["scenarios"]![0]!["recoveryDuration"] = duration;
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        var scenario = Assert.IsType<LightOnScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.Equal(TimeSpan.Parse(duration, System.Globalization.CultureInfo.InvariantCulture),
            scenario.RecoveryDuration);
        AssertSingleValidationError(plan,
            "Scenario.RecoveryDuration.Unexpected", "scenarios[0].recoveryDuration");
    }

    /// <summary>
    /// Verifies that a mixed plan retains all three concrete types, their order,
    /// and the distinct immediate and gradual recovery settings.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithTemperatureLightAndHumidityPreservesTypesOrderAndRecoverySettings()
    {
        var json = JsonNode.Parse(DoorPlanJson)!;
        var scenarios = json["scenarios"]!.AsArray();
        scenarios.Insert(0, JsonNode.Parse("""
            {
              "type": "HighTemperature",
              "name": "GradualHighTemperature",
              "startsAfter": "00:00:30",
              "duration": "00:04:00",
              "target": { "mode": "All" },
              "autoRecover": true,
              "recoveryDuration": "00:01:00",
              "abnormalMinimum": 30,
              "abnormalMaximum": 35,
              "maximumRisePerMeasurement": 2,
              "maximumRecoveryPerMeasurement": 1
            }
            """));
        scenarios.Add(JsonNode.Parse("""
            {
              "type": "HighHumidity",
              "name": "GradualHighHumidity",
              "startsAfter": "00:02:00",
              "duration": "00:03:00",
              "target": { "mode": "RandomCompatible", "count": 2 },
              "autoRecover": true,
              "recoveryDuration": "00:02:00",
              "abnormalMinimum": 90,
              "abnormalMaximum": 95,
              "maximumRisePerMeasurement": 3,
              "maximumRecoveryPerMeasurement": 2
            }
            """));
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        Assert.Equal(3, plan.Scenarios.Count);
        var temperature = Assert.IsType<HighTemperatureScenarioDefinition>(plan.Scenarios[0]);
        var light = Assert.IsType<LightOnScenarioDefinition>(plan.Scenarios[1]);
        var humidity = Assert.IsType<HighHumidityScenarioDefinition>(plan.Scenarios[2]);
        Assert.Equal(new HighTemperatureScenarioDefinition(
            "GradualHighTemperature", TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4),
            new ScenarioTargetDefinition(ScenarioTargetMode.All),
            true, TimeSpan.FromMinutes(1), 30, 35, 2, 1), temperature);
        Assert.Equal(new LightOnScenarioDefinition(
            "ScheduledLightOn", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: "LIGHT-001"),
            true, null), light);
        Assert.Equal(new HighHumidityScenarioDefinition(
            "GradualHighHumidity", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.RandomCompatible, Count: 2),
            true, TimeSpan.FromMinutes(2), 90, 95, 3, 2), humidity);
        AssertValid(plan);
    }

    /// <summary>Verifies successful public validation after reading a plan.</summary>
    /// <param name="plan">The deserialized plan to validate.</param>
    private static void AssertValid(SimulationPlanDefinition plan)
    {
        var result = new SimulationPlanValidator().Validate(plan);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>Verifies one expected diagnostic after successful deserialization.</summary>
    /// <param name="plan">The deserialized plan to validate.</param>
    /// <param name="code">The expected validation error code.</param>
    /// <param name="path">The expected JSON property path.</param>
    private static void AssertSingleValidationError(
        SimulationPlanDefinition plan, string code, string path)
    {
        var result = new SimulationPlanValidator().Validate(plan);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>Owns an isolated plans directory and its production reader.</summary>
    private sealed class TestPlanDirectory : IDisposable
    {
        private readonly string _contentRoot;
        private readonly string _plansDirectory;

        /// <summary>Creates a unique temporary content root for the current test.</summary>
        public TestPlanDirectory()
        {
            _contentRoot = Path.Combine(Path.GetTempPath(),
                $"AssetMonitoring-LightOn-{Guid.NewGuid():N}");
            _plansDirectory = Path.Combine(_contentRoot, "Configuration", "Plans");
            Directory.CreateDirectory(_plansDirectory);
            Reader = new JsonSimulationPlanReader(new TestHostEnvironment
            {
                ContentRootPath = _contentRoot
            });
        }

        /// <summary>Gets the production reader rooted in the temporary directory.</summary>
        public JsonSimulationPlanReader Reader { get; }

        /// <summary>Writes a plan using a file name independent of its JSON name.</summary>
        /// <param name="json">The JSON contents supplied to the production reader.</param>
        /// <returns>A task that completes when the plan file has been written.</returns>
        public Task WriteAsync(string json) => File.WriteAllTextAsync(
            Path.Combine(_plansDirectory, "selected-file.json"),
            json, TestContext.Current.CancellationToken);

        /// <summary>Removes the temporary files after all reader streams have closed.</summary>
        public void Dispose() => Directory.Delete(_contentRoot, recursive: true);
    }

    /// <summary>Supplies the content root required by the production reader.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <inheritdoc />
        public string EnvironmentName { get; set; } = Environments.Development;

        /// <inheritdoc />
        public string ApplicationName { get; set; } = nameof(JsonSimulationPlanReaderLightOnTests);

        /// <inheritdoc />
        public string ContentRootPath { get; set; } = string.Empty;

        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
