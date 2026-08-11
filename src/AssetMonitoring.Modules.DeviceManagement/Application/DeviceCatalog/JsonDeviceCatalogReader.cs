using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

/// <summary>
/// Reads and deserializes a device catalog from a JSON file.
/// </summary>
public sealed class JsonDeviceCatalogReader : IDeviceCatalogReader
{
    /// <inheritdoc />
    public async Task<DeviceCatalogDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = File.OpenRead(path);

        var document = await JsonSerializer.DeserializeAsync<DeviceCatalogDocument>(stream, options: SerializerOptions, cancellationToken: cancellationToken);

        if (document is null)
        {
            throw new JsonException("Device catalog JSON produced a null document.");
        }

        return document;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
}
