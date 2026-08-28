using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Domain.Devices;

public sealed class DeviceTests
{
    private static readonly DateTime RegisteredAtUtc =
        new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConstructorWithValidDataCreatesRegisteredDevice()
    {
        var capabilities = new[]
        {
            DeviceCapability.Temperature,
            DeviceCapability.Humidity
        };

        var device = CreateDevice(capabilities);

        Assert.NotEqual(Guid.Empty, device.Id);
        Assert.Equal("WH-001", device.Code);
        Assert.Equal("Warehouse sensor", device.Name);
        Assert.Equal("Sensor-X", device.HardwareModel);
        Assert.Equal("R1", device.HardwareRevision);
        Assert.Equal("1.0.0", device.FirmwareVersion);
        Assert.Equal("Warehouse A", device.Location);
        Assert.Equal(RegisteredAtUtc, device.RegisteredAtUtc);
        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
        Assert.Equal(2, device.Capabilities.Count);
        Assert.Contains(DeviceCapability.Temperature, device.Capabilities);
        Assert.Contains(DeviceCapability.Humidity, device.Capabilities);
    }

    [Fact]
    public void ConstructorWithMissingRequiredDataThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Device(
                string.Empty,
                "Warehouse sensor",
                "Sensor-X",
                "R1",
                "1.0.0",
                "Warehouse A",
                new[] { DeviceCapability.Temperature },
                RegisteredAtUtc));

        Assert.Equal(
            "Required device data is missing or invalid.",
            exception.Message);
    }

    [Fact]
    public void ConstructorWithNonUtcRegistrationTimeThrowsArgumentException()
    {
        var localTime = new DateTime(
            2026,
            8,
            12,
            10,
            0,
            0,
            DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() =>
            CreateDevice(registeredAtUtc: localTime));

        Assert.Equal("registeredAtUtc", exception.ParamName);
    }

    [Fact]
    public void ConstructorWithNullCapabilitiesThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new Device(
                "WH-001",
                "Warehouse sensor",
                "Sensor-X",
                "R1",
                "1.0.0",
                "Warehouse A",
                null!,
                RegisteredAtUtc));
    }

    [Fact]
    public void ConstructorWithEmptyCapabilitiesThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateDevice(capabilities: Array.Empty<DeviceCapability>()));
    }

    [Fact]
    public void ConstructorWithDuplicateCapabilitiesStoresEachCapabilityOnce()
    {
        var capabilities = new[]
        {
            DeviceCapability.Temperature,
            DeviceCapability.Temperature,
            DeviceCapability.Humidity
        };

        var device = CreateDevice(capabilities);

        Assert.Equal(2, device.Capabilities.Count);
        Assert.Contains(DeviceCapability.Temperature, device.Capabilities);
        Assert.Contains(DeviceCapability.Humidity, device.Capabilities);
    }

    [Fact]
    public void ActivateRegisteredDeviceChangesLifecycleAndReturnsTrue()
    {
        var device = CreateDevice();

        var changed = device.Activate();

        Assert.True(changed);
        Assert.Equal(DeviceLifecycle.Active, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void ActivateActiveDeviceReturnsFalseAndPreservesLifecycle()
    {
        var device = CreateDevice();
        device.Activate();

        var changed = device.Activate();

        Assert.False(changed);
        Assert.Equal(DeviceLifecycle.Active, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void ActivateRetiredDeviceThrowsAndPreservesRetirement()
    {
        var device = CreateDevice();
        var retiredAtUtc = RegisteredAtUtc.AddDays(1);
        device.Retire(retiredAtUtc);

        Assert.Throws<InvalidOperationException>(() => device.Activate());
        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
    }

    [Fact]
    public void RetireRegisteredDeviceChangesLifecycleAndStoresTimestamp()
    {
        var device = CreateDevice();
        var retiredAtUtc = RegisteredAtUtc.AddDays(1);

        var changed = device.Retire(retiredAtUtc);

        Assert.True(changed);
        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
    }

    [Fact]
    public void RetireAlreadyRetiredDeviceReturnsFalseAndPreservesTimestamp()
    {
        var device = CreateDevice();
        var firstRetirementUtc = RegisteredAtUtc.AddDays(1);
        var secondRetirementUtc = RegisteredAtUtc.AddDays(2);
        device.Retire(firstRetirementUtc);

        var changed = device.Retire(secondRetirementUtc);

        Assert.False(changed);
        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(firstRetirementUtc, device.RetiredAtUtc);
    }

    [Fact]
    public void RetireWithNonUtcTimestampThrowsArgumentException()
    {
        var device = CreateDevice();
        var unspecifiedTime = new DateTime(
            2026,
            8,
            13,
            10,
            0,
            0,
            DateTimeKind.Unspecified);

        var exception = Assert.Throws<ArgumentException>(() =>
            device.Retire(unspecifiedTime));

        Assert.Equal("retiredAtUtc", exception.ParamName);
        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void RestoreRetiredDeviceChangesLifecycleAndClearsTimestamp()
    {
        var device = CreateDevice();
        device.Retire(RegisteredAtUtc.AddDays(1));

        var changed = device.Restore();

        Assert.True(changed);
        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void RestoreRegisteredDeviceReturnsFalse()
    {
        var device = CreateDevice();

        var changed = device.Restore();

        Assert.False(changed);
        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.RetiredAtUtc);
    }

    [Fact]
    public void UpdateMetadataWithChangedValuesUpdatesCatalogManagedData()
    {
        var device = CreateDevice();

        var changed = device.UpdateMetadata(
            "Updated sensor",
            "Sensor-Y",
            "R2",
            "2.0.0",
            "Warehouse B",
            new[]
            {
                DeviceCapability.Humidity,
                DeviceCapability.DoorState
            });

        Assert.True(changed);
        Assert.Equal("Updated sensor", device.Name);
        Assert.Equal("Sensor-Y", device.HardwareModel);
        Assert.Equal("R2", device.HardwareRevision);
        Assert.Equal("2.0.0", device.FirmwareVersion);
        Assert.Equal("Warehouse B", device.Location);
        Assert.Equal(2, device.Capabilities.Count);
        Assert.Contains(DeviceCapability.Humidity, device.Capabilities);
        Assert.Contains(DeviceCapability.DoorState, device.Capabilities);
    }

    [Fact]
    public void UpdateMetadataWithSameValuesReturnsFalse()
    {
        var device = CreateDevice();

        var changed = device.UpdateMetadata(
            device.Name,
            device.HardwareModel,
            device.HardwareRevision,
            device.FirmwareVersion,
            device.Location,
            device.Capabilities);

        Assert.False(changed);
    }

    [Fact]
    public void UpdateMetadataTreatsCapabilitiesAsSet()
    {
        var device = CreateDevice(
            new[]
            {
                DeviceCapability.Temperature,
                DeviceCapability.Humidity
            });

        var changed = device.UpdateMetadata(
            device.Name,
            device.HardwareModel,
            device.HardwareRevision,
            device.FirmwareVersion,
            device.Location,
            new[]
            {
                DeviceCapability.Humidity,
                DeviceCapability.Temperature,
                DeviceCapability.Temperature
            });

        Assert.False(changed);
        Assert.Equal(2, device.Capabilities.Count);
    }

    [Fact]
    public void UpdateMetadataPreservesIdentityRegistrationAndLifecycle()
    {
        var device = CreateDevice();
        var retiredAtUtc = RegisteredAtUtc.AddDays(1);
        device.Retire(retiredAtUtc);
        var originalId = device.Id;
        var originalCode = device.Code;

        var changed = device.UpdateMetadata(
            "Updated sensor",
            "Sensor-Y",
            "R2",
            "2.0.0",
            "Warehouse B",
            new[] { DeviceCapability.Humidity });

        Assert.True(changed);
        Assert.Equal(originalId, device.Id);
        Assert.Equal(originalCode, device.Code);
        Assert.Equal(RegisteredAtUtc, device.RegisteredAtUtc);
        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
    }

    [Fact]
    public void UpdateMetadataWithMissingRequiredDataThrowsArgumentException()
    {
        var device = CreateDevice();

        Assert.Throws<ArgumentException>(() =>
            device.UpdateMetadata(
                string.Empty,
                "Sensor-Y",
                "R2",
                "2.0.0",
                "Warehouse B",
                new[] { DeviceCapability.Humidity }));
    }

    [Fact]
    public void UpdateMetadataWithNullCapabilitiesThrowsArgumentException()
    {
        var device = CreateDevice();

        Assert.Throws<ArgumentException>(() =>
            device.UpdateMetadata(
                "Updated sensor",
                "Sensor-Y",
                "R2",
                "2.0.0",
                "Warehouse B",
                null!));
    }

    [Fact]
    public void UpdateMetadataWithEmptyCapabilitiesThrowsArgumentException()
    {
        var device = CreateDevice();

        Assert.Throws<ArgumentException>(() =>
            device.UpdateMetadata(
                "Updated sensor",
                "Sensor-Y",
                "R2",
                "2.0.0",
                "Warehouse B",
                Array.Empty<DeviceCapability>()));
    }

    private static Device CreateDevice(
        IEnumerable<DeviceCapability>? capabilities = null,
        DateTime? registeredAtUtc = null)
    {
        return new Device(
            "WH-001",
            "Warehouse sensor",
            "Sensor-X",
            "R1",
            "1.0.0",
            "Warehouse A",
            capabilities ?? new[] { DeviceCapability.Temperature },
            registeredAtUtc ?? RegisteredAtUtc);
    }
}
