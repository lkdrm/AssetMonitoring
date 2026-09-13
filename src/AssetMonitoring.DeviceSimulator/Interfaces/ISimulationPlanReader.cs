using AssetMonitoring.DeviceSimulator.Scenarios;

namespace AssetMonitoring.DeviceSimulator.Interfaces;

/// <summary>
/// Reads device simulation plans from an external configuration source.
/// </summary>
public interface ISimulationPlanReader
{
    /// <summary>
    /// Asynchronously reads and deserializes the simulation plan with the
    /// specified name.
    /// </summary>
    /// <param name="planName">
    /// The simulation plan name without a file extension.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to cancel the asynchronous file operation.
    /// </param>
    /// <returns>
    /// The deserialized simulation plan definition.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="planName"/> is null, empty, whitespace, or
    /// contains an invalid plan name.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the requested simulation plan file does not exist.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the plan file contains no simulation plan.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Thrown when the plan contains malformed JSON or an unsupported scenario
    /// type.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled.
    /// </exception>
    Task<SimulationPlanDefinition> ReadAsync(string planName, CancellationToken cancellationToken = default);
}
