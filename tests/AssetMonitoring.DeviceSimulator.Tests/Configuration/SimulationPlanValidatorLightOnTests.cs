using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Configuration;

/// <summary>
/// Verifies immediate light recovery, shared scenario validation,
/// and preservation of gradual-recovery rules in mixed plans.
/// </summary>
public sealed class SimulationPlanValidatorLightOnTests
{
    private readonly SimulationPlanValidator _validator = new();

    /// <summary>
    /// Verifies that an automatically recovering light scenario accepts a null
    /// recovery duration for every supported target mode.
    /// </summary>
    /// <param name="mode">The device-selection mode.</param>
    /// <param name="count">The optional number of randomly selected devices.</param>
    /// <param name="deviceCode">The optional specific-device code.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All, null, null)]
    [InlineData(ScenarioTargetMode.RandomCompatible, 2, null)]
    [InlineData(ScenarioTargetMode.SpecificDevice, null, "LIGHT-001")]
    public void ValidateLightOnWithAutomaticRecoveryAndNullDurationIsValid(
        ScenarioTargetMode mode, int? count, string? deviceCode)
    {
        var scenario = CreateValidLightScenario() with
        {
            Target = new ScenarioTargetDefinition(mode, count, deviceCode)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertValid(result);
    }

    /// <summary>
    /// Verifies that any supplied recovery duration is unexpected for immediate
    /// light recovery, including zero and negative values.
    /// </summary>
    /// <param name="seconds">The supplied recovery duration in seconds.</param>
    [Theory]
    [InlineData(-60)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    public void ValidateLightOnWithAutomaticRecoveryAndSuppliedDurationReturnsOneUnexpectedError(
        int seconds)
    {
        var scenario = CreateValidLightScenario() with
        {
            RecoveryDuration = TimeSpan.FromSeconds(seconds)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.RecoveryDuration.Unexpected", "scenarios[0].recoveryDuration");
    }

    /// <summary>
    /// Verifies that the light-specific return does not bypass the shared
    /// rejection of scenarios without automatic recovery.
    /// </summary>
    [Fact]
    public void ValidateLightOnWithoutAutomaticRecoveryAndWithNullDurationReturnsNotSupportedError()
    {
        var scenario = CreateValidLightScenario() with { AutoRecover = false };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover");
    }

    /// <summary>
    /// Verifies that disabling light recovery and supplying a duration produces
    /// both relevant errors exactly once.
    /// </summary>
    /// <param name="seconds">The supplied recovery duration in seconds.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60)]
    public void ValidateLightOnWithoutAutomaticRecoveryAndWithSuppliedDurationReturnsBothErrorsOnce(
        int seconds)
    {
        var scenario = CreateValidLightScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = TimeSpan.FromSeconds(seconds)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover"),
            ("Scenario.RecoveryDuration.Unexpected", "scenarios[0].recoveryDuration"));
    }

    /// <summary>
    /// Verifies that immediate recovery preserves the shared start-delay rule.
    /// </summary>
    [Fact]
    public void ValidateLightOnWithNegativeStartDelayReturnsStartDelayError()
    {
        var scenario = CreateValidLightScenario() with
        {
            StartsAfter = TimeSpan.FromTicks(-1)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.StartsAfter.Invalid", "scenarios[0].startsAfter");
    }

    /// <summary>
    /// Verifies that the active light duration must remain greater than zero.
    /// </summary>
    /// <param name="seconds">The invalid active duration in seconds.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateLightOnWithNonPositiveActiveDurationReturnsDurationError(int seconds)
    {
        var scenario = CreateValidLightScenario() with
        {
            Duration = TimeSpan.FromSeconds(seconds)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.Duration.Invalid", "scenarios[0].duration");
    }

    /// <summary>
    /// Verifies that a light scenario still requires a device-selection target.
    /// </summary>
    [Fact]
    public void ValidateLightOnWithMissingTargetReturnsTargetRequiredError()
    {
        var scenario = CreateValidLightScenario() with { Target = null! };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.Target.Required", "scenarios[0].target");
    }

    /// <summary>
    /// Verifies that returning from light recovery validation does not suppress
    /// shared scenario errors or subsequent target validation.
    /// </summary>
    [Fact]
    public void ValidateLightOnWithSeveralInvalidPropertiesCollectsAllErrorsWithoutDuplicates()
    {
        var scenario = CreateValidLightScenario() with
        {
            Name = " ",
            StartsAfter = TimeSpan.FromSeconds(-1),
            Duration = TimeSpan.Zero,
            AutoRecover = false,
            RecoveryDuration = TimeSpan.FromMinutes(1),
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible, Count: 0, DeviceCode: "LIGHT-001")
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.Name.Required", "scenarios[0].name"),
            ("Scenario.StartsAfter.Invalid", "scenarios[0].startsAfter"),
            ("Scenario.Duration.Invalid", "scenarios[0].duration"),
            ("Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover"),
            ("Scenario.RecoveryDuration.Unexpected", "scenarios[0].recoveryDuration"),
            ("Scenario.Target.Count.Invalid", "scenarios[0].target.count"),
            ("Scenario.Target.DeviceCode.Unexpected", "scenarios[0].target.deviceCode"));
    }

    /// <summary>
    /// Verifies that light and numeric scenarios share case-insensitive name
    /// uniqueness within a plan.
    /// </summary>
    [Fact]
    public void ValidateLightOnWithNameDuplicatingAnotherScenarioTypeReturnsDuplicateNameError()
    {
        var light = CreateValidLightScenario();
        var temperature = CreateGradualScenario(useHumidity: false) with
        {
            Name = light.Name.ToUpperInvariant()
        };

        var result = _validator.Validate(CreatePlan(light, temperature));

        AssertSingleError(result,
            "Scenario.Name.Duplicate", "scenarios[1].name");
    }

    /// <summary>
    /// Verifies that valid mixed plans accept immediate light recovery alongside
    /// gradual numeric recovery regardless of the light scenario's position.
    /// </summary>
    /// <param name="lightIndex">The insertion index of the light scenario.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidateMixedPlanWithLightAndGradualRecoveryIsValid(int lightIndex)
    {
        var scenarios = new List<ScenarioDefinition>
        {
            CreateGradualScenario(useHumidity: false),
            CreateGradualScenario(useHumidity: true)
        };
        scenarios.Insert(lightIndex, CreateValidLightScenario());

        var result = _validator.Validate(CreatePlan(scenarios.ToArray()));

        AssertValid(result);
    }

    /// <summary>
    /// Verifies that the light exception does not relax the positive recovery
    /// duration required by subsequent temperature or humidity scenarios.
    /// </summary>
    /// <param name="useHumidity">Whether the numeric scenario affects humidity.</param>
    /// <param name="recoverySeconds">The absent or non-positive recovery duration.</param>
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(true, null)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void ValidateMixedPlanWithInvalidGradualRecoveryReturnsOnlyGradualDurationError(
        bool useHumidity, int? recoverySeconds)
    {
        var numeric = CreateGradualScenario(useHumidity,
            recoverySeconds: recoverySeconds);

        var result = _validator.Validate(CreatePlan(CreateValidLightScenario(), numeric));

        AssertSingleError(result,
            "Scenario.RecoveryDuration.Invalid", "scenarios[1].recoveryDuration");
    }

    /// <summary>
    /// Verifies that moving the automatic-recovery check preserves the existing
    /// rejection rules for temperature and humidity without duplicating errors.
    /// </summary>
    /// <param name="useHumidity">Whether the numeric scenario affects humidity.</param>
    /// <param name="recoverySeconds">The optional duration supplied with recovery disabled.</param>
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 60)]
    [InlineData(true, null)]
    [InlineData(true, 60)]
    public void ValidateMixedPlanWithoutGradualAutomaticRecoveryPreservesExistingErrors(
        bool useHumidity, int? recoverySeconds)
    {
        var numeric = CreateGradualScenario(useHumidity,
            autoRecover: false, recoverySeconds: recoverySeconds);

        var result = _validator.Validate(CreatePlan(CreateValidLightScenario(), numeric));

        if (recoverySeconds is null)
        {
            AssertSingleError(result,
                "Scenario.AutoRecover.NotSupported", "scenarios[1].autoRecover");
        }
        else
        {
            AssertErrors(result,
                ("Scenario.AutoRecover.NotSupported", "scenarios[1].autoRecover"),
                ("Scenario.RecoveryDuration.Unexpected", "scenarios[1].recoveryDuration"));
        }
    }

    /// <summary>
    /// Verifies that light diagnostics use the scenario's actual plan index
    /// after otherwise valid temperature and humidity definitions.
    /// </summary>
    [Fact]
    public void ValidateMixedPlanWithInvalidLightRecoveryUsesLightScenarioIndex()
    {
        var light = CreateValidLightScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = TimeSpan.FromSeconds(30)
        };

        var result = _validator.Validate(CreatePlan(
            CreateGradualScenario(useHumidity: false),
            CreateGradualScenario(useHumidity: true), light));

        AssertErrors(result,
            ("Scenario.AutoRecover.NotSupported", "scenarios[2].autoRecover"),
            ("Scenario.RecoveryDuration.Unexpected", "scenarios[2].recoveryDuration"));
    }

    /// <summary>Creates a light definition with immediate automatic recovery.</summary>
    /// <returns>A valid definition with no recovery duration.</returns>
    private static LightOnScenarioDefinition CreateValidLightScenario() => new(
        "ScheduledLightOn", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
        new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice, DeviceCode: "LIGHT-001"),
        true, null);

    /// <summary>Creates a numeric definition while varying its recovery configuration.</summary>
    /// <param name="useHumidity">Whether to create a humidity definition.</param>
    /// <param name="autoRecover">Whether automatic recovery is enabled.</param>
    /// <param name="recoverySeconds">The optional recovery duration in seconds.</param>
    /// <returns>A definition with otherwise valid numeric settings.</returns>
    private static ScenarioDefinition CreateGradualScenario(
        bool useHumidity, bool autoRecover = true, int? recoverySeconds = 120)
    {
        TimeSpan? recoveryDuration = recoverySeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
        var target = new ScenarioTargetDefinition(ScenarioTargetMode.All);

        return useHumidity
            ? new HighHumidityScenarioDefinition(
                "GradualHighHumidity", TimeSpan.Zero, TimeSpan.FromMinutes(3),
                target, autoRecover, recoveryDuration, 90, 95, 3, 3)
            : new HighTemperatureScenarioDefinition(
                "GradualHighTemperature", TimeSpan.Zero, TimeSpan.FromMinutes(3),
                target, autoRecover, recoveryDuration, 30, 35, 2, 2);
    }

    /// <summary>Wraps definitions in a plan with valid plan-level properties.</summary>
    /// <param name="scenarios">The definitions in their intended plan order.</param>
    /// <returns>A plan ready for public validation.</returns>
    private static SimulationPlanDefinition CreatePlan(params ScenarioDefinition[] scenarios) =>
        new("DoorValidation", 42, scenarios);

    /// <summary>Verifies a successful result with no diagnostics.</summary>
    /// <param name="result">The result returned by the production validator.</param>
    private static void AssertValid(SimulationPlanValidationResult result)
    {
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>Verifies exactly one diagnostic with its code and JSON path.</summary>
    /// <param name="result">The result returned by the production validator.</param>
    /// <param name="code">The expected stable diagnostic code.</param>
    /// <param name="path">The expected scenario-property path.</param>
    private static void AssertSingleError(
        SimulationPlanValidationResult result, string code, string path) =>
        AssertErrors(result, (code, path));

    /// <summary>
    /// Verifies the complete set of diagnostics without relying on their order,
    /// including the absence of duplicated code-and-path pairs.
    /// </summary>
    /// <param name="result">The result returned by the production validator.</param>
    /// <param name="expected">Every expected diagnostic code and JSON path.</param>
    private static void AssertErrors(
        SimulationPlanValidationResult result, params (string Code, string Path)[] expected)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expected.Length, result.Errors.Count);
        Assert.Equal(result.Errors.Count,
            result.Errors.Select(error => (error.Code, error.Path)).Distinct().Count());
        foreach (var error in expected)
        {
            Assert.Contains(result.Errors, actual =>
                actual.Code == error.Code && actual.Path == error.Path);
        }
        Assert.All(result.Errors, error =>
            Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }
}
