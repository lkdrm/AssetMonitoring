using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssetMonitoring.DeviceSimulator.Tests.Configuration;

/// <summary>
/// Verifies high-humidity JSON definitions through the production plan reader.
/// These tests exercise deserialization independently of plan validation.
/// </summary>
public sealed class JsonSimulationPlanReaderHighHumidityTests
{
    private const string HumidityPlanJson = """
        {
          "name": "HumidityPlan",
          "seed": 42,
          "scenarios": [
            {
              "type": "HighHumidity",
              "name": "GradualHighHumidity",
              "startsAfter": "00:01:00",
              "duration": "00:03:00",
              "target": { "mode": "SpecificDevice", "deviceCode": "WH-001" },
              "autoRecover": true,
              "recoveryDuration": "00:02:00",
              "abnormalMinimum": 90,
              "abnormalMaximum": 95,
              "maximumRisePerMeasurement": 3,
              "maximumRecoveryPerMeasurement": 2.5
            }
          ]
        }
        """;

    /// <summary>
    /// Verifies that the discriminator creates the humidity definition and
    /// preserves every configured property, including the containing plan.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithHighHumidityDeserializesEveryConfiguredProperty()
    {
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(HumidityPlanJson);

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        Assert.Equal("HumidityPlan", plan.Name);
        Assert.Equal(42, plan.Seed);
        var scenario = Assert.IsType<HighHumidityScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.Equal("GradualHighHumidity", scenario.Name);
        Assert.Equal(TimeSpan.FromMinutes(1), scenario.StartsAfter);
        Assert.Equal(TimeSpan.FromMinutes(3), scenario.Duration);
        Assert.Equal(ScenarioTargetMode.SpecificDevice, scenario.Target.Mode);
        Assert.Equal("WH-001", scenario.Target.DeviceCode);
        Assert.Null(scenario.Target.Count);
        Assert.True(scenario.AutoRecover);
        Assert.Equal(TimeSpan.FromMinutes(2), scenario.RecoveryDuration);
        Assert.Equal(90d, scenario.AbnormalMinimum);
        Assert.Equal(95d, scenario.AbnormalMaximum);
        Assert.Equal(3d, scenario.MaximumRisePerMeasurement);
        Assert.Equal(2.5d, scenario.MaximumRecoveryPerMeasurement);
    }

    /// <summary>
    /// Verifies that humidity scenarios support the remaining target modes
    /// and preserve their optional target properties.
    /// </summary>
    /// <param name="mode">The target-mode string supplied in JSON.</param>
    /// <param name="count">The optional number of randomly selected devices.</param>
    [Theory]
    [InlineData("All", null)]
    [InlineData("RandomCompatible", 2)]
    public async Task ReadAsyncWithHighHumidityPreservesTargetMode(string mode, int? count)
    {
        var json = JsonNode.Parse(HumidityPlanJson)!;
        json["scenarios"]![0]!["target"] = new JsonObject
        {
            ["mode"] = mode,
            ["count"] = count
        };
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        var scenario = Assert.IsType<HighHumidityScenarioDefinition>(Assert.Single(plan.Scenarios));
        Assert.Equal(Enum.Parse<ScenarioTargetMode>(mode), scenario.Target.Mode);
        Assert.Equal(count, scenario.Target.Count);
        Assert.Null(scenario.Target.DeviceCode);
    }

    /// <summary>
    /// Verifies that mixed plans retain both concrete definition types and
    /// their original order without confusing temperature and humidity settings.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithMixedScenarioTypesPreservesTypesOrderAndSettings()
    {
        var json = JsonNode.Parse(HumidityPlanJson)!;
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
              "abnormalMinimum": 40,
              "abnormalMaximum": 45,
              "maximumRisePerMeasurement": 2,
              "maximumRecoveryPerMeasurement": 1
            }
            """));
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        var plan = await directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken);

        Assert.Equal(2, plan.Scenarios.Count);
        var temperature = Assert.IsType<HighTemperatureScenarioDefinition>(plan.Scenarios[0]);
        var humidity = Assert.IsType<HighHumidityScenarioDefinition>(plan.Scenarios[1]);
        Assert.Equal(new HighTemperatureScenarioDefinition(
            "GradualHighTemperature", TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(4),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(1),
            40, 45, 2, 1), temperature);
        Assert.Equal(new HighHumidityScenarioDefinition(
            "GradualHighHumidity", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: "WH-001"),
            true, TimeSpan.FromMinutes(2), 90, 95, 3, 2.5), humidity);
    }

    /// <summary>
    /// Verifies that an unknown discriminator remains a JSON read failure
    /// instead of silently creating a known scenario type.
    /// </summary>
    [Fact]
    public async Task ReadAsyncWithUnknownScenarioTypeThrowsJsonException()
    {
        var json = JsonNode.Parse(HumidityPlanJson)!;
        json["scenarios"]![0]!["type"] = "UnknownHumidity";
        using var directory = new TestPlanDirectory();
        await directory.WriteAsync(json.ToJsonString());

        await Assert.ThrowsAsync<JsonException>(() => directory.Reader.ReadAsync(
            "selected-file", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Owns an isolated plans directory and removes it after each test.
    /// </summary>
    private sealed class TestPlanDirectory : IDisposable
    {
        private readonly string _contentRoot;
        private readonly string _plansDirectory;

        /// <summary>
        /// Creates a production reader rooted in a unique temporary directory.
        /// </summary>
        public TestPlanDirectory()
        {
            _contentRoot = Path.Combine(Path.GetTempPath(),
                $"AssetMonitoring-HighHumidity-{Guid.NewGuid():N}");
            _plansDirectory = Path.Combine(_contentRoot, "Configuration", "Plans");
            Directory.CreateDirectory(_plansDirectory);
            Reader = new JsonSimulationPlanReader(new TestHostEnvironment
            {
                ContentRootPath = _contentRoot
            });
        }

        /// <summary>Gets the production reader used by the current test.</summary>
        public JsonSimulationPlanReader Reader { get; }

        /// <summary>Writes the selected plan using its file name rather than its JSON name.</summary>
        /// <param name="json">The JSON supplied to the production reader.</param>
        /// <returns>A task that completes when the file has been written.</returns>
        public Task WriteAsync(string json) => File.WriteAllTextAsync(
            Path.Combine(_plansDirectory, "selected-file.json"),
            json, TestContext.Current.CancellationToken);

        /// <summary>Removes the temporary directory after all read streams have closed.</summary>
        public void Dispose() => Directory.Delete(_contentRoot, recursive: true);
    }

    /// <summary>
    /// Supplies the content root required by the production reader.
    /// </summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <inheritdoc />
        public string EnvironmentName { get; set; } = Environments.Development;

        /// <inheritdoc />
        public string ApplicationName { get; set; } = nameof(JsonSimulationPlanReaderHighHumidityTests);

        /// <inheritdoc />
        public string ContentRootPath { get; set; } = string.Empty;

        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
