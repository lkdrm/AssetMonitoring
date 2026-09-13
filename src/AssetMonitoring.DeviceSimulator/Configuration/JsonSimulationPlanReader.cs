using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Scenarios;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetMonitoring.DeviceSimulator.Configuration;

/// <summary>
/// Reads device simulation plans from JSON files located in the configured
/// plans directory.
/// </summary>
public sealed class JsonSimulationPlanReader : ISimulationPlanReader
{
    private readonly string _plansDirectory;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="JsonSimulationPlanReader"/> class.
    /// </summary>
    /// <param name="hostEnvironment">
    /// The hosting environment used to resolve the simulation plans directory.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="hostEnvironment"/> is null.
    /// </exception>
    public JsonSimulationPlanReader(IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment, nameof(hostEnvironment));

        _plansDirectory = Path.Combine(hostEnvironment.ContentRootPath, "Configuration", "Plans");
    }

    /// <inheritdoc />
    public async Task<SimulationPlanDefinition> ReadAsync(string planName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planName, nameof(planName));

        var filePath = Path.Combine(_plansDirectory, $"{planName}.json");

        await using var stream = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        }); 

        var document = await JsonSerializer.DeserializeAsync<SimulationPlanDefinition>(stream, options: SerializerOptions, cancellationToken);

        return document is null ? throw new InvalidDataException("Simulation plan JSON produced a null document.") : document; 
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter<ScenarioTargetMode>(allowIntegerValues: false) }
    };
}
