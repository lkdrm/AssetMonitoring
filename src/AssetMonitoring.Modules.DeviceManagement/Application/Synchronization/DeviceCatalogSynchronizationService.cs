using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;

/// <summary>
/// Coordinates validation and persistence synchronization
/// of the device catalog.
/// </summary>
public sealed class DeviceCatalogSynchronizationService
{
    private readonly DeviceCatalogLoader _catalogLoader;
    private readonly IDeviceRepository _deviceRepository;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="DeviceCatalogSynchronizationService"/> class.
    /// </summary>
    /// <param name="catalogLoader">The service used to load and validate the catalog.</param>
    /// <param name="deviceRepository">The repository used to access and persist devices.</param>
    /// <param name="timeProvider">The provider used to obtain the current UTC time.</param>
    public DeviceCatalogSynchronizationService(DeviceCatalogLoader catalogLoader, IDeviceRepository deviceRepository, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(catalogLoader);
        ArgumentNullException.ThrowIfNull(deviceRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _catalogLoader = catalogLoader;
        _deviceRepository = deviceRepository;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Asynchronously loads, validates, and synchronizes a device catalog.
    /// </summary>
    /// <param name="path">The path to the device catalog file.</param>
    /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
    /// <returns>The result of the catalog validation and synchronization.</returns>
    public async Task<DeviceCatalogSynchronizationResult> SynchronizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var loadResult = await _catalogLoader.LoadAsync(path, cancellationToken);

        if (!loadResult.IsValid)
        {
            return new DeviceCatalogSynchronizationResult(loadResult.ValidationResult, 0, 0, 0, 0, 0);
        }

        var existingDevices = await _deviceRepository.GetAllAsync(cancellationToken);
        var existingDevicesByCode = existingDevices.ToDictionary(
            device => device.Code,
            StringComparer.OrdinalIgnoreCase);

        var synchronizedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var catalogCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var retired = 0;
        var restored = 0;
        var hasChanges = false;

        foreach (var device in loadResult.Document.Devices)
        {
            catalogCodes.Add(device.Code);

            if (!existingDevicesByCode.TryGetValue(device.Code, out var existingDevice))
            {
                var newDevice = new Device(
                     device.Code, device.Name, device.HardwareModel, device.HardwareRevision,
                     device.FirmwareVersion, device.Location, device.Capabilities, synchronizedAtUtc);

                _deviceRepository.Add(newDevice);
                created++;
                hasChanges = true;
                continue;
            }

            var deviceChanged = false;

            if (existingDevice.Restore())
            {
                restored++;
                deviceChanged = true;
            }

            if (existingDevice.UpdateMetadata(device.Name, device.HardwareModel, device.HardwareRevision, device.FirmwareVersion, device.Location, device.Capabilities))
            {
                updated++;
                deviceChanged = true;
            }

            if (!deviceChanged)
            {
                unchanged++;
            }

            hasChanges |= deviceChanged;
        }

        foreach (var existingDevice in existingDevices)
        {
            if (catalogCodes.Contains(existingDevice.Code))
            {
                continue;
            }

            if (existingDevice.Retire(synchronizedAtUtc))
            {
                retired++;
                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            await _deviceRepository.SaveChangesAsync(cancellationToken);
        }

        return new DeviceCatalogSynchronizationResult(loadResult.ValidationResult, created, updated, unchanged, retired, restored);
    }
}
