using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Configuration.Validation;

/// <summary>
/// Validates simulation plans and collects every discovered configuration
/// error.
/// </summary>
public sealed class SimulationPlanValidator
{
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
            ValidateHighTemperature(temperatureScenarioDefinition, path, errors);
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
    /// Validates high-temperature bounds and per-measurement rise and recovery limits.
    /// </summary>
    private static void ValidateHighTemperature(HighTemperatureScenarioDefinition scenario, string path, List<SimulationPlanValidationError> errors)
    {
        var minimumIsFinite = double.IsFinite(scenario.AbnormalMinimum);
        var maximumIsFinite = double.IsFinite(scenario.AbnormalMaximum);

        if (!minimumIsFinite)
        {
            errors.Add(new("Scenario.HighTemperature.Minimum.NotFinite",
                $"{path}.abnormalMinimum",
                "Abnormal minimum temperature must be a finite number."));
        }

        if (!maximumIsFinite)
        {
            errors.Add(new("Scenario.HighTemperature.Maximum.NotFinite",
                $"{path}.abnormalMaximum",
                "Abnormal maximum temperature must be a finite number."));
        }

        if (minimumIsFinite && maximumIsFinite && scenario.AbnormalMinimum >= scenario.AbnormalMaximum)
        {
            errors.Add(new("Scenario.HighTemperature.Range.Invalid",
                $"{path}.abnormalMinimum",
                "Abnormal minimum temperature must be lower than the abnormal maximum temperature."));
        }

        if (!double.IsFinite(scenario.MaximumRisePerMeasurement) || scenario.MaximumRisePerMeasurement <= 0)
        {
            errors.Add(new("Scenario.HighTemperature.MaximumRise.Invalid",
                $"{path}.maximumRisePerMeasurement",
                "Maximum temperature rise per measurement must be finite and greater than zero."));
        }

        if (scenario.AutoRecover && (!double.IsFinite(scenario.MaximumRecoveryPerMeasurement) || scenario.MaximumRecoveryPerMeasurement <= 0))
        {
            errors.Add(new("Scenario.HighTemperature.MaximumRecovery.Invalid",
                $"{path}.maximumRecoveryPerMeasurement",
                "Maximum temperature recovery per measurement must be finite and greater than zero when automatic recovery is enabled."));
        }
    }
}
