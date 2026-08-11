using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;

namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;

/// <summary>
/// Coordinates loading and validating a device catalog.
/// </summary>
public sealed class DeviceCatalogLoader
{
    private readonly DeviceCatalogValidator _validator;
    private readonly IDeviceCatalogReader _reader;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceCatalogLoader"/> class.
    /// </summary>
    /// <param name="catalogValidator">The validator used to verify catalog business rules.</param>
    /// <param name="catalogReader">The reader used to load and deserialize the catalog.</param>
    public DeviceCatalogLoader(DeviceCatalogValidator catalogValidator, IDeviceCatalogReader catalogReader)
    {
        ArgumentNullException.ThrowIfNull(catalogValidator);
        ArgumentNullException.ThrowIfNull(catalogReader);
        _validator = catalogValidator;
        _reader = catalogReader;
    }

    /// <summary>
    /// Asynchronously loads and validates a device catalog.
    /// </summary>
    /// <param name="path">The path to the device catalog file.</param>
    /// <param name="cancellationToken"> A token used to cancel the asynchronous operation.</param>
    /// <returns>A result containing the loaded document and its validation result.</returns>
    public async Task<DeviceCatalogLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await _reader.ReadAsync(path, cancellationToken);
        var validation = _validator.Validate(document);

        return new DeviceCatalogLoadResult(document, validation);
    }
}
