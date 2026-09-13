using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Interfaces;

namespace AssetMonitoring.DeviceSimulator.Load;

/// <summary>
/// Coordinates reading and validation of simulation plans.
/// </summary>
public sealed class SimulationPlanLoader
{
    private readonly ISimulationPlanReader _reader;
    private readonly SimulationPlanValidator _validator;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="SimulationPlanLoader"/> class.
    /// </summary>
    /// <param name="reader">
    /// The reader used to load simulation plans.
    /// </param>
    /// <param name="validator">
    /// The validator used to check simulation plan settings.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="reader"/> or
    /// <paramref name="validator"/> is null.
    /// </exception>
    public SimulationPlanLoader(ISimulationPlanReader reader, SimulationPlanValidator validator)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(validator);

        _reader = reader;
        _validator = validator;
    }

    /// <summary>
    /// Asynchronously loads a simulation plan and validates its settings.
    /// </summary>
    /// <param name="planName">
    /// The name of the simulation plan to load.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous read operation.
    /// </param>
    /// <returns>
    /// The loaded plan and its validation result, including any
    /// configuration errors.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="planName"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="planName"/> is empty or whitespace.
    /// </exception>
    public async Task<SimulationPlanLoadResult> LoadAsync(string planName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planName);

        var plan = await _reader.ReadAsync(planName, cancellationToken);
        var validation = _validator.Validate(plan);

        return new SimulationPlanLoadResult(plan, validation);
    }
}
