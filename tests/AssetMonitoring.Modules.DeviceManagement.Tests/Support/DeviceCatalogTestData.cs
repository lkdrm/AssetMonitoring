using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Support;

internal static class DeviceCatalogTestData
{
    internal static readonly DateTime RegisteredAtUtc =
        new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    internal static DeviceCatalogDocument CreateValidDocument(int deviceCount = 10)
    {
        return new DeviceCatalogDocument
        {
            Devices = Enumerable.Range(1, deviceCount)
                .Select(CreateItem)
                .ToList()
        };
    }

    internal static DeviceCatalogItem CreateItem(int number = 1)
    {
        return new DeviceCatalogItem
        {
            Code = $"WH-{number:000}",
            Name = $"Warehouse sensor {number}",
            HardwareModel = "Sensor-X",
            HardwareRevision = "R1",
            FirmwareVersion = "1.0.0",
            Location = $"Warehouse zone {number}",
            Capabilities =
            [
                DeviceCapability.Temperature,
                DeviceCapability.Humidity
            ]
        };
    }

    internal static Device CreateDevice(
        DeviceCatalogItem item,
        DateTime? registeredAtUtc = null)
    {
        return new Device(
            item.Code,
            item.Name,
            item.HardwareModel,
            item.HardwareRevision,
            item.FirmwareVersion,
            item.Location,
            item.Capabilities,
            registeredAtUtc ?? RegisteredAtUtc);
    }
}
