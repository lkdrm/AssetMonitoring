using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Configuration.Validation;

/// <summary>
/// Validates simulation plans and collects every discovered configuration
/// error.
/// </summary>
public sealed class SimulationPlanValidator
{
    private const string HighTemperatureCodePrefix = "Scenario.HighTemperature";
    private const string TemperatureMetricName = "temperature";
    private const string HighHumidityCodePrefix = "Scenario.HighHumidity";
    private const string HumidityMetricName = "humidity";

    /// <summary>
    /// Validates the supplied simulation plan and returns all discovered
    /// errors.
    /// </summary>
    /// <param name="plan">
    /// The simulation plan to validate.
    /// </param>
    /// <returns>
    /// A structured result containing every validation error. An empty error
    /// collection indicates a valid plan.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="plan"/> is null.
    /// </exception>
    public SimulationPlanValidationResult Validate(SimulationPlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan, nameof(plan));

        var errors = new List<SimulationPlanValidationError>();

        ValidatePlan(plan, errors);
        if (plan.Scenarios is null)
        {
            return new SimulationPlanValidationResult(errors.ToArray());
        }

        var scenarioNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < plan.Scenarios.Count; i++)
        {
            var scenario = plan.Scenarios[i];
            var path = $"scenarios[{i}]";

            if (scenario is null)
            {
                errors.Add(new("Scenario.Required",
                    path,
                    "Scenario definition cannot be null."));
                continue;
            }
            ValidateScenario(scenario, path, scenarioNames, errors);
        }

        return new SimulationPlanValidationResult(errors.ToArray());
    }

    /// <summary>
    /// Validates plan-level properties: the name and the presence of the scenarios collection.
    /// </summary>
    private static void ValidatePlan(SimulationPlanDefinition plan, List<SimulationPlanValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(plan.Name))
        {
            errors.Add(new("Plan.Name.Required",
                "name",
                "Simulation plan name is required."));
        }
        if (plan.Scenarios is null)
        {
            errors.Add(new("Plan.Scenarios.Required",
                "scenarios",
                "Simulation plan scenarios collection is required."));
        }
    }

    /// <summary>
    /// Validates one scenario and adds every discovered error with a JSON path rooted at <paramref name="path"/>.
    /// </summary>
    private static void ValidateScenario(ScenarioDefinition scenario, string path, HashSet<string> scenarioNames, List<SimulationPlanValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            errors.Add(new("Scenario.Name.Required",
                $"{path}.name",
                "Scenario name is required."));
        }
        else if (!scenarioNames.Add(scenario.Name))
        {
            errors.Add(new("Scenario.Name.Duplicate",
                $"{path}.name",
                $"Scenario name '{scenario.Name}' is duplicated."));
        }

        if (scenario.Target is null)
        {
            errors.Add(new("Scenario.Target.Required",
                $"{path}.target",
                "Scenario target is required."));
        }

        if (scenario.StartsAfter < TimeSpan.Zero)
        {
            errors.Add(new("Scenario.StartsAfter.Invalid",
                $"{path}.startsAfter",
                "Scenario start delay cannot be negative."));
        }

        if (scenario.Duration <= TimeSpan.Zero)
        {
            errors.Add(new("Scenario.Duration.Invalid",
                $"{path}.duration",
                "Scenario duration must be greater than zero."));
        }

        ValidateRecovery(scenario, path, errors);

        if (scenario.Target is not null)
        {
            ValidateTarget(scenario.Target, $"{path}.target", errors);
        }

        if (scenario is HighTemperatureScenarioDefinition temperatureScenarioDefinition)
        {
            ValidateGradualDeviation(HighTemperatureCodePrefix, TemperatureMetricName, temperatureScenarioDefinition.AbnormalMinimum, temperatureScenarioDefinition.AbnormalMaximum,
                path, temperatureScenarioDefinition.AutoRecover, temperatureScenarioDefinition.MaximumRisePerMeasurement, temperatureScenarioDefinition.MaximumRecoveryPerMeasurement, errors);
        }
        else if (scenario is HighHumidityScenarioDefinition humidityScenarioDefinition)
        {
            ValidateGradualDeviation(HighHumidityCodePrefix, HumidityMetricName, humidityScenarioDefinition.AbnormalMinimum, humidityScenarioDefinition.AbnormalMaximum,
                path, humidityScenarioDefinition.AutoRecover, humidityScenarioDefinition.MaximumRisePerMeasurement, humidityScenarioDefinition.MaximumRecoveryPerMeasurement, errors);

            ValidateHumidityBounds(humidityScenarioDefinition, path, errors);
        }
    }

    /// <summary>
    /// Validates the combination of automatic recovery and recovery duration.
    /// </summary>
    private static void ValidateRecovery(ScenarioDefinition scenario, string path, List<SimulationPlanValidationError> errors)
    {
        if (scenario.AutoRecover)
        {
            if (scenario.RecoveryDuration is null || scenario.RecoveryDuration <= TimeSpan.Zero)
            {
                errors.Add(new(
                    "Scenario.RecoveryDuration.Invalid",
                    $"{path}.recoveryDuration",
                    "Recovery duration must be provided and greater than zero when automatic recovery is enabled."));
            }

            return;
        }

        errors.Add(new("Scenario.AutoRecover.NotSupported",
            $"{path}.autoRecover",
            "Scenarios without automatic recovery are not supported yet."));

        if (scenario.RecoveryDuration is not null)
        {
            errors.Add(new(
                "Scenario.RecoveryDuration.Unexpected",
                $"{path}.recoveryDuration",
                "Recovery duration must be null when automatic recovery is disabled."));
        }
    }

    /// <summary>
    /// Validates that the target properties match the selected target mode.
    /// </summary>
    private static void ValidateTarget(ScenarioTargetDefinition target, string path, List<SimulationPlanValidationError> errors)
    {
        switch (target.Mode)
        {
            case ScenarioTargetMode.All:
                if (target.Count is not null)
                {
                    errors.Add(new("Scenario.Target.Count.Unexpected",
                        $"{path}.count",
                         "Target count must be null when all compatible devices are selected."));
                }
                if (!string.IsNullOrWhiteSpace(target.DeviceCode))
                {
                    errors.Add(new("Scenario.Target.DeviceCode.Unexpected",
                        $"{path}.deviceCode",
                        "Device code must be null when all compatible devices are selected."));
                }
                break;

            case ScenarioTargetMode.RandomCompatible:
                if (target.Count is null or < 1 or > 10)
                {
                    errors.Add(new("Scenario.Target.Count.Invalid",
                        $"{path}.count",
                        "Target count must be between 1 and 10 for random compatible selection."));
                }
                if (!string.IsNullOrWhiteSpace(target.DeviceCode))
                {
                    errors.Add(new("Scenario.Target.DeviceCode.Unexpected",
                        $"{path}.deviceCode",
                        "Device code must be null for random compatible selection."));
                }
                break;

            case ScenarioTargetMode.SpecificDevice:
                if (target.Count is not null)
                {
                    errors.Add(new("Scenario.Target.Count.Unexpected",
                        $"{path}.count",
                        "Target count must be null when a specific device is selected."));
                }
                if (string.IsNullOrWhiteSpace(target.DeviceCode))
                {
                    errors.Add(new("Scenario.Target.DeviceCode.Required",
                        $"{path}.deviceCode",
                        "Device code is required when a specific device is selected."));
                }
                break;

            default:
                errors.Add(new("Scenario.Target.Mode.Unsupported",
                    $"{path}.mode",
                    $"Target mode '{target.Mode}' is not supported."));
                break;
        }
    }

    /// <summary>
    /// Validates abnormal numeric target bounds and per-measurement rise
    /// and recovery limits, collecting all discovered errors.
    /// </summary>
    /// <remarks>
    /// Non-finite bounds are reported individually and excluded from
    /// range-order comparisons. The recovery limit is checked only
    /// when automatic recovery is enabled.
    /// </remarks>
    /// <param name="codePrefix">
    /// The stable scenario-specific prefix used to build validation error codes.
    /// </param>
    /// <param name="metricName">
    /// The human-readable metric name used in validation error messages.
    /// </param>
    /// <param name="abnormalMinimum">
    /// The lower bound used to select the abnormal target value.
    /// Must be finite and lower than <paramref name="abnormalMaximum"/>.
    /// </param>
    /// <param name="abnormalMaximum">
    /// The upper bound used to select the abnormal target value.
    /// Must be finite and greater than <paramref name="abnormalMinimum"/>.
    /// </param>
    /// <param name="path">
    /// The JSON path of the scenario being validated.
    /// </param>
    /// <param name="autoRecover">
    /// Indicates whether automatic recovery is enabled and its
    /// per-measurement limit must be validated.
    /// </param>
    /// <param name="maximumRise">
    /// The maximum increase per measurement.
    /// Must be finite and greater than zero.
    /// </param>
    /// <param name="maximumRecovery">
    /// The maximum decrease per recovery measurement.
    /// Must be finite and greater than zero when automatic recovery is enabled.
    /// </param>
    /// <param name="errors">
    /// The collection to which discovered validation errors are appended.
    /// Existing errors are preserved.
    /// </param>
    private static void ValidateGradualDeviation(string codePrefix, string metricName, double abnormalMinimum, double abnormalMaximum, string path, bool autoRecover, double maximumRise, double maximumRecovery, List<SimulationPlanValidationError> errors)
    {
        var minimumIsFinite = double.IsFinite(abnormalMinimum);
        var maximumIsFinite = double.IsFinite(abnormalMaximum);

        if (!minimumIsFinite)
        {
            errors.Add(new($"{codePrefix}.Minimum.NotFinite", $"{path}.abnormalMinimum", $"Abnormal minimum {metricName} must be a finite number."));
        }

        if (!maximumIsFinite)
        {
            errors.Add(new($"{codePrefix}.Maximum.NotFinite", $"{path}.abnormalMaximum", $"Abnormal maximum {metricName} must be a finite number."));
        }

        if (minimumIsFinite && maximumIsFinite && abnormalMinimum >= abnormalMaximum)
        {
            errors.Add(new($"{codePrefix}.Range.Invalid", $"{path}.abnormalMinimum", $"Abnormal minimum {metricName} must be lower than the abnormal maximum {metricName}."));
        }

        if (!double.IsFinite(maximumRise) || maximumRise <= 0)
        {
            errors.Add(new($"{codePrefix}.MaximumRise.Invalid", $"{path}.maximumRisePerMeasurement",
                $"Maximum {metricName} rise per measurement must be finite and greater than zero."));
        }

        if (autoRecover && (!double.IsFinite(maximumRecovery) || maximumRecovery <= 0))
        {
            errors.Add(new($"{codePrefix}.MaximumRecovery.Invalid", $"{path}.maximumRecoveryPerMeasurement",
                $"Maximum {metricName} recovery per measurement must be finite and greater than zero when automatic recovery is enabled."));
        }
    }

    /// <summary>
    /// Validates the humidity-specific limits by reporting a finite minimum
    /// below zero or a finite maximum above one hundred percent.
    /// </summary>
    /// <remarks>
    /// Zero and one hundred percent are permitted.
    /// This method complements the finite-value and range-order checks
    /// performed by <see cref="ValidateGradualDeviation"/>.
    /// Non-finite values are excluded to avoid duplicate errors.
    /// </remarks>
    /// <param name="scenario">
    /// The high-humidity definition whose abnormal bounds are validated.
    /// </param>
    /// <param name="path">
    /// The JSON path of the scenario being validated.
    /// </param>
    /// <param name="errors">
    /// The collection to which humidity-bound errors are appended.
    /// Existing errors are preserved.
    /// </param>
    private static void ValidateHumidityBounds(HighHumidityScenarioDefinition scenario, string path, List<SimulationPlanValidationError> errors)
    {
        if (double.IsFinite(scenario.AbnormalMinimum) && scenario.AbnormalMinimum < 0)
        {
            errors.Add(new($"{HighHumidityCodePrefix}.Range.OutOfBounds", $"{path}.abnormalMinimum",
                $"Abnormal minimum {HumidityMetricName} cannot be lower than 0 %."));
        }
        if (double.IsFinite(scenario.AbnormalMaximum) && scenario.AbnormalMaximum > 100)
        {
            errors.Add(new($"{HighHumidityCodePrefix}.Range.OutOfBounds", $"{path}.abnormalMaximum",
                $"Abnormal maximum {HumidityMetricName} cannot be greater than 100 %."));
        }
    }
}
