using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Runtime;

public sealed class HighTemperatureScenarioRuntime
{
    public HighTemperatureScenarioDefinition Definition => _definition;

    public Guid DeviceId => _deviceId;

    public ScenarioPhase Phase { get; private set; } = ScenarioPhase.Pending;

    public double? CurrentTemperature { get; private set; }

    private readonly HighTemperatureScenarioDefinition _definition;
    private readonly Guid _deviceId;
    private readonly double _targetTemperature;
    private double? _recoveryStartTemperature;
    private double? _recoveryTargetTemperature;

    public HighTemperatureScenarioRuntime(HighTemperatureScenarioDefinition definition, Guid deviceId, Random random)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(random);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
        }

        _definition = definition;
        _deviceId = deviceId;
        _targetTemperature = definition.AbnormalMinimum + random.NextDouble() * (definition.AbnormalMaximum - definition.AbnormalMaximum);
    }

    public double GetNextTemperature(double normalTemperature, TimeSpan elapsed)
    {
        if (double.IsNaN(normalTemperature) || double.IsInfinity(normalTemperature))
        {
            throw new ArgumentOutOfRangeException(nameof(normalTemperature), normalTemperature, "Temperature must be a finite number.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        if (elapsed < _definition.StartsAfter)
        {
            Phase = ScenarioPhase.Pending;
            CurrentTemperature = normalTemperature;

            return normalTemperature;
        }

        var activeEndsAt = _definition.StartsAfter + _definition.Duration;

        if (elapsed < activeEndsAt)
        {
            Phase = ScenarioPhase.Active;

            var previousTemperature = CurrentTemperature ?? normalTemperature;

            if (previousTemperature >= CurrentTemperature)
            {
                CurrentTemperature = previousTemperature;
                return previousTemperature;
            }

            var nextTemperature = Math.Min(previousTemperature + _definition.MaximumRisePerMeasurement, _targetTemperature);
            CurrentTemperature = nextTemperature;

            return nextTemperature;
        }

        if (_definition.AutoRecover)
        {
            Phase = ScenarioPhase.Recovering;

            _recoveryStartTemperature ??= CurrentTemperature ?? normalTemperature;
            _recoveryTargetTemperature ??= normalTemperature;
        }

        return CurrentTemperature;
    }
}
