using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Tests.Configuration;

/// <summary>
/// Verifies high-humidity configuration rules, shared scenario validation,
/// and metric-specific diagnostics after extracting gradual-deviation validation.
/// </summary>
public sealed class SimulationPlanValidatorHighHumidityTests
{
    private readonly SimulationPlanValidator _validator = new();

    /// <summary>Verifies that humidity supports every valid target mode.</summary>
    /// <param name="mode">The device-selection mode.</param>
    /// <param name="count">The optional random-selection count.</param>
    /// <param name="deviceCode">The optional specific-device code.</param>
    [Theory]
    [InlineData(ScenarioTargetMode.All, null, null)]
    [InlineData(ScenarioTargetMode.RandomCompatible, 2, null)]
    [InlineData(ScenarioTargetMode.SpecificDevice, null, "WH-001")]
    public void ValidateHighHumidityWithValidTargetIsValid(
        ScenarioTargetMode mode, int? count, string? deviceCode)
    {
        var scenario = CreateValidScenario() with
        {
            Target = new ScenarioTargetDefinition(mode, count, deviceCode)
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertValid(result);
    }

    /// <summary>Verifies ordered humidity bounds, including the permitted endpoints.</summary>
    /// <param name="minimum">The abnormal lower bound.</param>
    /// <param name="maximum">The abnormal upper bound.</param>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 100)]
    [InlineData(90, 95)]
    [InlineData(99, 100)]
    [InlineData(0, 0.1)]
    public void ValidateHighHumidityWithOrderedBoundsWithinZeroToHundredIsValid(
        double minimum, double maximum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertValid(result);
    }

    /// <summary>Verifies that each non-finite minimum produces only its finite-value error.</summary>
    /// <param name="minimum">The non-finite abnormal minimum.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateHighHumidityWithNonFiniteMinimumReturnsOnlyMinimumError(double minimum)
    {
        var scenario = CreateValidScenario() with { AbnormalMinimum = minimum };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.Minimum.NotFinite", "scenarios[0].abnormalMinimum");
    }

    /// <summary>Verifies that each non-finite maximum produces only its finite-value error.</summary>
    /// <param name="maximum">The non-finite abnormal maximum.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValidateHighHumidityWithNonFiniteMaximumReturnsOnlyMaximumError(double maximum)
    {
        var scenario = CreateValidScenario() with { AbnormalMaximum = maximum };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.Maximum.NotFinite", "scenarios[0].abnormalMaximum");
    }

    /// <summary>Verifies that invalid bounds are collected without duplicate range errors.</summary>
    /// <param name="minimum">The non-finite lower bound.</param>
    /// <param name="maximum">The non-finite upper bound.</param>
    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.PositiveInfinity, double.NegativeInfinity)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity)]
    public void ValidateHighHumidityWithBothBoundsNonFiniteReturnsBothErrors(
        double minimum, double maximum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.HighHumidity.Minimum.NotFinite", "scenarios[0].abnormalMinimum"),
            ("Scenario.HighHumidity.Maximum.NotFinite", "scenarios[0].abnormalMaximum"));
    }

    /// <summary>Verifies that equal or reversed finite bounds are rejected.</summary>
    /// <param name="minimum">The lower bound that is not below the maximum.</param>
    /// <param name="maximum">The upper bound.</param>
    [Theory]
    [InlineData(90, 90)]
    [InlineData(95, 90)]
    [InlineData(100, 100)]
    public void ValidateHighHumidityWithEqualOrReversedBoundsReturnsRangeError(
        double minimum, double maximum)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.Range.Invalid", "scenarios[0].abnormalMinimum");
    }

    /// <summary>Verifies that ordered bounds outside the humidity domain are rejected.</summary>
    /// <param name="minimum">The abnormal lower bound.</param>
    /// <param name="maximum">The abnormal upper bound.</param>
    /// <param name="property">The property identified by the domain error.</param>
    [Theory]
    [InlineData(-1, 50, "abnormalMinimum")]
    [InlineData(50, 101, "abnormalMaximum")]
    [InlineData(-double.Epsilon, 100, "abnormalMinimum")]
    [InlineData(0, 100.001, "abnormalMaximum")]
    [InlineData(-2, -1, "abnormalMinimum")]
    [InlineData(101, 102, "abnormalMaximum")]
    public void ValidateHighHumidityWithOutOfBoundsRangeReturnsFieldSpecificError(
        double minimum, double maximum, string property)
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.Range.OutOfBounds", $"scenarios[0].{property}");
    }

    /// <summary>Verifies that both humidity domain violations are collected.</summary>
    [Fact]
    public void ValidateHighHumidityWithBothBoundsOutsideDomainReturnsBothErrors()
    {
        var scenario = CreateValidScenario() with
        {
            AbnormalMinimum = -1,
            AbnormalMaximum = 101
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.HighHumidity.Range.OutOfBounds", "scenarios[0].abnormalMinimum"),
            ("Scenario.HighHumidity.Range.OutOfBounds", "scenarios[0].abnormalMaximum"));
    }

    /// <summary>Verifies that rise limits must be finite and greater than zero.</summary>
    /// <param name="maximumRise">The invalid rise limit.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateHighHumidityWithInvalidMaximumRiseReturnsRiseError(double maximumRise)
    {
        var scenario = CreateValidScenario() with { MaximumRisePerMeasurement = maximumRise };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.MaximumRise.Invalid", "scenarios[0].maximumRisePerMeasurement");
    }

    /// <summary>Verifies that enabled recovery requires a finite positive recovery limit.</summary>
    /// <param name="maximumRecovery">The invalid recovery limit.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateHighHumidityWithInvalidMaximumRecoveryReturnsRecoveryError(double maximumRecovery)
    {
        var scenario = CreateValidScenario() with { MaximumRecoveryPerMeasurement = maximumRecovery };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result,
            "Scenario.HighHumidity.MaximumRecovery.Invalid", "scenarios[0].maximumRecoveryPerMeasurement");
    }

    /// <summary>Verifies that all finite positive step limits are accepted.</summary>
    /// <param name="step">The positive rise and recovery limit.</param>
    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(0.1)]
    [InlineData(3)]
    [InlineData(double.MaxValue)]
    public void ValidateHighHumidityWithFinitePositiveStepLimitsIsValid(double step)
    {
        var scenario = CreateValidScenario() with
        {
            MaximumRisePerMeasurement = step,
            MaximumRecoveryPerMeasurement = step
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertValid(result);
    }

    /// <summary>
    /// Verifies that an unused recovery limit adds no error while disabled
    /// automatic recovery is still reported as unsupported.
    /// </summary>
    /// <param name="maximumRecovery">The unused invalid recovery limit.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateHighHumidityWithoutRecoveryIgnoresUnusedRecoveryLimit(double maximumRecovery)
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = null,
            MaximumRecoveryPerMeasurement = maximumRecovery
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover");
    }

    /// <summary>
    /// Verifies that disabled recovery with a supplied duration reports both
    /// shared recovery errors without validating the unused recovery limit.
    /// </summary>
    [Fact]
    public void ValidateHighHumidityWithoutRecoveryAndWithDurationReturnsBothRecoveryErrors()
    {
        var scenario = CreateValidScenario() with
        {
            AutoRecover = false,
            RecoveryDuration = TimeSpan.FromMinutes(2),
            MaximumRecoveryPerMeasurement = double.NaN
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.AutoRecover.NotSupported", "scenarios[0].autoRecover"),
            ("Scenario.RecoveryDuration.Unexpected", "scenarios[0].recoveryDuration"));
    }

    /// <summary>Verifies that enabled recovery requires a positive duration.</summary>
    /// <param name="seconds">The missing or non-positive recovery duration.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateHighHumidityWithMissingOrInvalidRecoveryDurationReturnsDurationError(int? seconds)
    {
        var scenario = CreateValidScenario() with
        {
            RecoveryDuration = seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.RecoveryDuration.Invalid", "scenarios[0].recoveryDuration");
    }

    /// <summary>Verifies that humidity scenarios still require a positive active duration.</summary>
    /// <param name="seconds">The non-positive active duration.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateHighHumidityWithNonPositiveDurationReturnsDurationError(int seconds)
    {
        var scenario = CreateValidScenario() with { Duration = TimeSpan.FromSeconds(seconds) };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.Duration.Invalid", "scenarios[0].duration");
    }

    /// <summary>Verifies that humidity scenarios reject negative start delays.</summary>
    [Fact]
    public void ValidateHighHumidityWithNegativeStartDelayReturnsStartError()
    {
        var scenario = CreateValidScenario() with { StartsAfter = TimeSpan.FromSeconds(-1) };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.StartsAfter.Invalid", "scenarios[0].startsAfter");
    }

    /// <summary>Verifies that humidity scenarios still require a non-blank name.</summary>
    [Fact]
    public void ValidateHighHumidityWithBlankNameReturnsNameError()
    {
        var scenario = CreateValidScenario() with { Name = " " };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.Name.Required", "scenarios[0].name");
    }

    /// <summary>Verifies that a missing humidity target is reported without throwing.</summary>
    [Fact]
    public void ValidateHighHumidityWithMissingTargetReturnsTargetError()
    {
        var scenario = CreateValidScenario() with { Target = null! };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertSingleError(result, "Scenario.Target.Required", "scenarios[0].target");
    }

    /// <summary>
    /// Verifies that shared, numeric, and humidity-domain errors are all
    /// collected when several independent properties are invalid.
    /// </summary>
    [Fact]
    public void ValidateHighHumidityWithSeveralInvalidPropertiesReturnsEveryError()
    {
        var scenario = CreateValidScenario() with
        {
            Name = " ",
            StartsAfter = TimeSpan.FromSeconds(-1),
            Duration = TimeSpan.Zero,
            Target = new ScenarioTargetDefinition(ScenarioTargetMode.SpecificDevice),
            AbnormalMinimum = double.NaN,
            AbnormalMaximum = 101,
            MaximumRisePerMeasurement = 0,
            MaximumRecoveryPerMeasurement = 0
        };

        var result = _validator.Validate(CreatePlan(scenario));

        AssertErrors(result,
            ("Scenario.Name.Required", "scenarios[0].name"),
            ("Scenario.StartsAfter.Invalid", "scenarios[0].startsAfter"),
            ("Scenario.Duration.Invalid", "scenarios[0].duration"),
            ("Scenario.Target.DeviceCode.Required", "scenarios[0].target.deviceCode"),
            ("Scenario.HighHumidity.Minimum.NotFinite", "scenarios[0].abnormalMinimum"),
            ("Scenario.HighHumidity.Range.OutOfBounds", "scenarios[0].abnormalMaximum"),
            ("Scenario.HighHumidity.MaximumRise.Invalid", "scenarios[0].maximumRisePerMeasurement"),
            ("Scenario.HighHumidity.MaximumRecovery.Invalid", "scenarios[0].maximumRecoveryPerMeasurement"));
    }

    /// <summary>
    /// Verifies stable temperature messages and humidity-specific diagnostics
    /// in mixed plans, including the correct scenario indexes.
    /// </summary>
    /// <param name="nonFiniteBounds">
    /// Whether to check finite-value errors rather than reversed-range errors.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateMixedNumericScenariosPreservesCodesPathsAndMessages(bool nonFiniteBounds)
    {
        var minimum = nonFiniteBounds ? double.NaN : 95d;
        var maximum = nonFiniteBounds ? double.NaN : 90d;
        var temperature = new HighTemperatureScenarioDefinition(
            "Temperature", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
            new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(2),
            minimum, maximum, 0, 0);
        var humidity = CreateValidScenario() with
        {
            Name = "Humidity",
            AbnormalMinimum = minimum,
            AbnormalMaximum = maximum,
            MaximumRisePerMeasurement = 0,
            MaximumRecoveryPerMeasurement = 0
        };
        (string Suffix, string Property, string Message)[] expected = nonFiniteBounds
            ? new[]
            {
                ("Minimum.NotFinite", "abnormalMinimum", "Abnormal minimum {0} must be a finite number."),
                ("Maximum.NotFinite", "abnormalMaximum", "Abnormal maximum {0} must be a finite number."),
                ("MaximumRise.Invalid", "maximumRisePerMeasurement", "Maximum {0} rise per measurement must be finite and greater than zero."),
                ("MaximumRecovery.Invalid", "maximumRecoveryPerMeasurement", "Maximum {0} recovery per measurement must be finite and greater than zero when automatic recovery is enabled.")
            }
            : new[]
            {
                ("Range.Invalid", "abnormalMinimum", "Abnormal minimum {0} must be lower than the abnormal maximum {0}."),
                ("MaximumRise.Invalid", "maximumRisePerMeasurement", "Maximum {0} rise per measurement must be finite and greater than zero."),
                ("MaximumRecovery.Invalid", "maximumRecoveryPerMeasurement", "Maximum {0} recovery per measurement must be finite and greater than zero when automatic recovery is enabled.")
            };

        var result = _validator.Validate(CreatePlan(temperature, humidity));

        Assert.False(result.IsValid);
        Assert.Equal(expected.Length * 2, result.Errors.Count);
        var metrics = new[]
        {
            (Index: 0, Prefix: "Scenario.HighTemperature", Name: "temperature"),
            (Index: 1, Prefix: "Scenario.HighHumidity", Name: "humidity")
        };
        foreach (var metric in metrics)
        {
            foreach (var diagnostic in expected)
            {
                var error = Assert.Single(result.Errors.Where(error =>
                    error.Code == $"{metric.Prefix}.{diagnostic.Suffix}"
                    && error.Path == $"scenarios[{metric.Index}].{diagnostic.Property}"));
                Assert.Equal(string.Format(diagnostic.Message, metric.Name), error.Message);
            }
        }
    }

    /// <summary>Creates a humidity definition with valid shared and numeric settings.</summary>
    /// <returns>A valid automatically recovering high-humidity definition.</returns>
    private static HighHumidityScenarioDefinition CreateValidScenario() => new(
        "GradualHighHumidity", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3),
        new ScenarioTargetDefinition(ScenarioTargetMode.All), true, TimeSpan.FromMinutes(2),
        90, 95, 3, 3);

    /// <summary>Creates a named plan containing the supplied scenarios in their original order.</summary>
    /// <param name="scenarios">The definitions to validate.</param>
    /// <returns>A plan with valid plan-level settings.</returns>
    private static SimulationPlanDefinition CreatePlan(params ScenarioDefinition[] scenarios) =>
        new("HumidityValidation", 42, scenarios);

    /// <summary>Verifies that validation succeeded without any errors.</summary>
    /// <param name="result">The validation result to inspect.</param>
    private static void AssertValid(SimulationPlanValidationResult result)
    {
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>Verifies one error, its precise location, and a non-blank message.</summary>
    /// <param name="result">The validation result to inspect.</param>
    /// <param name="code">The expected stable error code.</param>
    /// <param name="path">The expected JSON path.</param>
    private static void AssertSingleError(
        SimulationPlanValidationResult result, string code, string path)
    {
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>Verifies the complete set of errors without requiring a particular order.</summary>
    /// <param name="result">The validation result to inspect.</param>
    /// <param name="expected">Every expected code and JSON-path pair.</param>
    private static void AssertErrors(
        SimulationPlanValidationResult result, params (string Code, string Path)[] expected)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expected.Length, result.Errors.Count);
        foreach (var error in expected)
        {
            Assert.Contains(result.Errors, actual =>
                actual.Code == error.Code && actual.Path == error.Path);
        }
        Assert.All(result.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Message)));
    }
}
