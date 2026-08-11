using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

/// <summary>
/// Validates device catalog data against the catalog business rules.
/// </summary>
public sealed class DeviceCatalogValidator
{
    /// <summary>
    /// Validates the specified device catalog and collects all detected errors.
    /// </summary>
    /// <param name="document">Device catalog document to validate.</param>
    /// <returns>
    /// A validation result containing all errors found in the catalog.
    /// The result is valid when no errors were detected.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="document"/> is null.
    /// </exception>
    public DeviceCatalogValidationResult Validate(DeviceCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<DeviceCatalogValidationError>();

        var deviceCount = document.Devices.Count;

        if (deviceCount != 10)
        {
            errors.Add(new DeviceCatalogValidationError("CatalogDeviceCount", Message: $"Catalog must contain exactly 10 devices, but contains {deviceCount}.", PropertyName: nameof(document.Devices)));
        }

        ValidateDevices(document.Devices, errors, deviceCount);

        return new DeviceCatalogValidationResult(errors);
    }

    private static void ValidateDevices(IReadOnlyList<DeviceCatalogItem> devices, List<DeviceCatalogValidationError> errors, int deviceCount)
    {
        var seenDeviceCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < deviceCount; i++)
        {
            var device = devices[i];

            var requiredFields = new[]
            {
                (Value: device.Code, PropertyName: nameof(device.Code)),
                (Value: device.Name, PropertyName: nameof(device.Name)),
                (Value: device.HardwareModel, PropertyName: nameof(device.HardwareModel)),
                (Value: device.HardwareRevision, PropertyName: nameof(device.HardwareRevision)),
                (Value: device.FirmwareVersion, PropertyName: nameof(device.FirmwareVersion)),
                (Value: device.Location, PropertyName: nameof(device.Location))
            };

            if (!string.IsNullOrWhiteSpace(device.Code) && !seenDeviceCodes.Add(device.Code))
            {
                errors.Add(new DeviceCatalogValidationError(
                    ErrorCode: "DuplicateDeviceCode",
                    Message: $"Device contains duplicate code '{device.Code}'.",
                    DeviceCode: device.Code,
                    PropertyName: nameof(device.Code)));
            }

            foreach (var (value, propertyName) in requiredFields)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                errors.Add(new DeviceCatalogValidationError(
                    ErrorCode: "RequiredFieldMissing",
                    Message:
                        $"Device at index {i} has an empty required field " +
                        $"'{propertyName}'.",
                    DeviceCode: device.Code,
                    PropertyName: propertyName));
            }

            if (device.Capabilities is null || device.Capabilities.Count == 0)
            {
                errors.Add(new DeviceCatalogValidationError(
                    ErrorCode: "DeviceCapabilitiesMissing",
                    Message: $"Device at index {i} must contain at least one capability.",
                    DeviceCode: device.Code,
                    PropertyName: nameof(device.Capabilities)));

                continue;
            }

            var capabilities = new HashSet<DeviceCapability>();

            foreach (var capability in device.Capabilities)
            {
                if (capabilities.Add(capability))
                {
                    continue;
                }

                errors.Add(new DeviceCatalogValidationError(
                    ErrorCode: "DuplicateCapability",
                    Message: $"Device contains duplicate capability '{capability}'.",
                    DeviceCode: device.Code,
                    PropertyName: nameof(device.Capabilities)));
            }
        }
    }
}
