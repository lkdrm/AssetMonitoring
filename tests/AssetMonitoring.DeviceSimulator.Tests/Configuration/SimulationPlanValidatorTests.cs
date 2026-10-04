using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Configuration;

public sealed class SimulationPlanValidatorTests
{
    private readonly SimulationPlanValidator _validator = new();

    [Fact]
    public void ValidateWithNullPlanThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _validator.Validate(null!));
    }

    [Fact]
    public void ValidateWithNormalPlanContainingNoScenariosReturnsValidResult()
    {
        var plan = new SimulationPlanDefinition(
            "NormalOperation",
            42,
            Array.Empty<ScenarioDefinition>());

        var result = _validator.Validate(plan);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateWithValidHighTemperatureScenarioReturnsValidResult()
    {
        var result = _validator.Validate(CreateValidPlan());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateWithMissingPlanNameReturnsRequiredNameError()
    {
        var plan = CreateValidPlan() with { Name = " " };

        var result = _validator.Validate(plan);

        AssertSingleError(result, "Plan.Name.Required", "name");
    }

    [Fact]
    public void ValidateWithNullScenariosReturnsRequiredScenariosError()
    {
        var plan = CreateValidPlan() with { Scenarios = null! };

        var result = _validator.Validate(plan);

        AssertSingleError(
            result,
            "Plan.Scenarios.Required",
            "scenarios");
    }

    [Fact]
    public void ValidateWithNullScenarioReturnsRequiredScenarioError()
    {
        var plan = CreatePlan(new ScenarioDefinition[] { null! });

        var result = _validator.Validate(plan);

        AssertSingleError(result, "Scenario.Required", "scenarios[0]");
    }

    [Fact]
    public void ValidateWithMissingScenarioNameReturnsRequiredNameError()
    {
        var scenario = CreateValidScenario() with { Name = string.Empty };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Name.Required",
            "scenarios[0].name");
    }

    [Fact]
    public void ValidateWithDuplicateScenarioNamesIgnoringCaseReturnsDuplicateNameError()
    {
        var first = CreateValidScenario() with { Name = "TemperatureFailure" };
        var second = CreateValidScenario() with { Name = "temperaturefailure" };

        var result = _validator.Validate(CreatePlan(first, second));

        AssertSingleError(
            result,
            "Scenario.Name.Duplicate",
            "scenarios[1].name");
    }

    [Fact]
    public void ValidateWithNegativeStartDelayReturnsInvalidStartDelayError()
    {
        var scenario = CreateValidScenario() with
        {
            StartsAfter = TimeSpan.FromSeconds(-1)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.StartsAfter.Invalid",
            "scenarios[0].startsAfter");
    }

    [Fact]
    public void ValidateWithZeroStartDelayReturnsValidResult()
    {
        var scenario = CreateValidScenario() with
        {
            StartsAfter = TimeSpan.Zero
        };

        var result = _validator.Validate(CreatePlan(scenario));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [MemberData(nameof(NonPositiveDurations))]
    public void ValidateWithNonPositiveDurationReturnsInvalidDurationError(
        TimeSpan duration)
    {
        var scenario = CreateValidScenario() with { Duration = duration };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Duration.Invalid",
            "scenarios[0].duration");
    }

    [Fact]
    public void ValidateWithNullTargetReturnsRequiredTargetError()
    {
        var scenario = CreateValidScenario() with { Target = null! };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.Required",
            "scenarios[0].target");
    }

    [Fact]
    public void ValidateWithAutomaticRecoveryAndNullDurationReturnsInvalidRecoveryDurationError()
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = true,
            RecoveryDuration = null
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.RecoveryDuration.Invalid",
            "scenarios[0].recoveryDuration");
    }

    [Theory]
    [MemberData(nameof(NonPositiveDurations))]
    public void ValidateWithAutomaticRecoveryAndNonPositiveDurationReturnsInvalidRecoveryDurationError(
        TimeSpan recoveryDuration)
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = true,
            RecoveryDuration = recoveryDuration
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.RecoveryDuration.Invalid",
            "scenarios[0].recoveryDuration");
    }

    /// <summary>
    /// Verifies that disabled automatic recovery and an unexpected
    /// recovery duration are both reported.
    /// </summary>
    [Fact]
    public void ValidateWithoutAutomaticRecoveryAndWithDurationReturnsBothErrors()
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = TimeSpan.FromMinutes(2)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);

        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.AutoRecover.NotSupported" &&
            error.Path == "scenarios[0].autoRecover");

        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.RecoveryDuration.Unexpected" &&
            error.Path == "scenarios[0].recoveryDuration");

        Assert.All(result.Errors, error =>
            Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }

    [Fact]
    public void ValidateWithAllTargetReturnsValidResult()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(ScenarioTargetMode.All)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateWithAllTargetAndCountReturnsUnexpectedCountError()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.All,
                Count: 1)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.Count.Unexpected",
            "scenarios[0].target.count");
    }

    [Fact]
    public void ValidateWithAllTargetAndDeviceCodeReturnsUnexpectedDeviceCodeError()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.All,
                DeviceCode: "WH-001")
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.DeviceCode.Unexpected",
            "scenarios[0].target.deviceCode");
    }

    [Fact]
    public void ValidateWithRandomCompatibleTargetReturnsValidResult()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible,
                Count: 1)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(11)]
    public void ValidateWithRandomCompatibleTargetAndInvalidCountReturnsInvalidCountError(
        int? count)
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible,
                Count: count)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.Count.Invalid",
            "scenarios[0].target.count");
    }

    [Fact]
    public void ValidateWithRandomCompatibleTargetAndDeviceCodeReturnsUnexpectedDeviceCodeError()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible,
                Count: 1,
                DeviceCode: "WH-001")
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.DeviceCode.Unexpected",
            "scenarios[0].target.deviceCode");
    }

    [Fact]
    public void ValidateWithSpecificDeviceTargetReturnsValidResult()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice,
                DeviceCode: "WH-001")
        };

        var result = _validator.Validate(CreatePlan(scenario));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateWithSpecificDeviceTargetAndMissingCodeReturnsRequiredDeviceCodeError(
        string? deviceCode)
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice,
                DeviceCode: deviceCode)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.DeviceCode.Required",
            "scenarios[0].target.deviceCode");
    }

    [Fact]
    public void ValidateWithSpecificDeviceTargetAndCountReturnsUnexpectedCountError()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                ScenarioTargetMode.SpecificDevice,
                Count: 1,
                DeviceCode: "WH-001")
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.Count.Unexpected",
            "scenarios[0].target.count");
    }

    [Fact]
    public void ValidateWithUnsupportedTargetModeReturnsUnsupportedModeError()
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(
                (ScenarioTargetMode)999)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.Target.Mode.Unsupported",
            "scenarios[0].target.mode");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateWithNonFiniteAbnormalMinimumReturnsNotFiniteMinimumError(
        double minimum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.HighTemperature.Minimum.NotFinite",
            "scenarios[0].abnormalMinimum");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateWithNonFiniteAbnormalMaximumReturnsNotFiniteMaximumError(
        double maximum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.HighTemperature.Maximum.NotFinite",
            "scenarios[0].abnormalMaximum");
    }

    [Theory]
    [InlineData(35, 35)]
    [InlineData(36, 35)]
    public void ValidateWithInvalidAbnormalRangeReturnsInvalidRangeError(
        double minimum,
        double maximum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.HighTemperature.Range.Invalid",
            "scenarios[0].abnormalMinimum");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateWithInvalidMaximumRiseReturnsInvalidMaximumRiseError(
        double maximumRise)
    {
        var scenario = CreateValidScenario() with
        {
            MaximumRisePerMeasurement = maximumRise
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.HighTemperature.MaximumRise.Invalid",
            "scenarios[0].maximumRisePerMeasurement");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateWithAutomaticRecoveryAndInvalidMaximumRecoveryReturnsInvalidMaximumRecoveryError(
        double maximumRecovery)
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = true,
            RecoveryDuration = TimeSpan.FromMinutes(2),
            MaximumRecoveryPerMeasurement = maximumRecovery
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.HighTemperature.MaximumRecovery.Invalid",
            "scenarios[0].maximumRecoveryPerMeasurement");
    }

    /// <summary>
    /// Verifies that disabled automatic recovery is rejected
    /// without reporting an error for its unused recovery limit.
    /// </summary>
    [Fact]
    public void ValidateWithoutAutomaticRecoveryAndUnusedMaximumRecoveryReturnsNotSupportedError()
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = null,
            MaximumRecoveryPerMeasurement = double.NaN
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(
            result,
            "Scenario.AutoRecover.NotSupported",
            "scenarios[0].autoRecover");
    }

    [Fact]
    public void ValidateWithSeveralInvalidPropertiesReturnsEveryDiscoveredError()
    {
        var scenario = CreateValidScenario() with
        {
            Name = " ",
            StartsAfter = TimeSpan.FromMinutes(-1),
            Duration = TimeSpan.Zero,
            Target = null!,
            RecoveryDuration = null,
            AbnormalMinimum = double.NaN,
            MaximumRisePerMeasurement = 0,
            MaximumRecoveryPerMeasurement = 0
        };
        var plan = CreatePlan(scenario) with { Name = "" };

        var result = _validator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Equal(9, result.Errors.Count);
        Assert.Contains(result.Errors, error =>
            error.Code == "Plan.Name.Required");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.Name.Required");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.StartsAfter.Invalid");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.Duration.Invalid");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.Target.Required");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.RecoveryDuration.Invalid");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.HighTemperature.Minimum.NotFinite");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.HighTemperature.MaximumRise.Invalid");
        Assert.Contains(result.Errors, error =>
            error.Code == "Scenario.HighTemperature.MaximumRecovery.Invalid");
    }

    public static TheoryData<TimeSpan> NonPositiveDurations => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromMinutes(-1)
    };

    private static SimulationPlanDefinition CreateValidPlan()
    {
        return CreatePlan(CreateValidScenario());
    }

    private static SimulationPlanDefinition CreatePlan(
        params ScenarioDefinition[] scenarios)
    {
        return new SimulationPlanDefinition(
            "WarehouseSimulation",
            42,
            scenarios);
    }

    private static HighTemperatureScenarioDefinition CreateValidScenario()
    {
        return new HighTemperatureScenarioDefinition(
            "GradualHighTemperature",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(
                ScenarioTargetMode.RandomCompatible,
                Count: 1),
            AutoRecover: true,
            RecoveryDuration: TimeSpan.FromMinutes(2),
            AbnormalMinimum: 30,
            AbnormalMaximum: 35,
            MaximumRisePerMeasurement: 2,
            MaximumRecoveryPerMeasurement: 2);
    }

    private static void AssertSingleError(
        SimulationPlanValidationResult result,
        string expectedCode,
        string expectedPath)
    {
        var error = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedPath, error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}
