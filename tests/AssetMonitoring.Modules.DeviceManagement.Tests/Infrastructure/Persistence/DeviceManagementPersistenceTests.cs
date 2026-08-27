using Microsoft.EntityFrameworkCore.Infrastructure;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Infrastructure.Persistence;

public sealed class DeviceManagementPersistenceTests
{
    [Fact]
    public void ModelContainsExpectedDeviceMapping()
    {
        using var database = new SqliteDeviceManagementDatabase();
        using var context = database.CreateDbContext();

        var model = context.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(Device));

        Assert.NotNull(entityType);
        Assert.Equal("Devices", entityType.GetTableName());
        Assert.Equal("device_management", entityType.GetSchema());

        var idProperty = entityType.FindProperty(nameof(Device.Id));
        Assert.NotNull(idProperty);
        Assert.Equal(ValueGenerated.Never, idProperty.ValueGenerated);

        var codeProperty = entityType.FindProperty(nameof(Device.Code));
        Assert.NotNull(codeProperty);
        Assert.False(codeProperty.IsNullable);
        Assert.Equal(50, codeProperty.GetMaxLength());
        Assert.Equal(
            "Latin1_General_100_CI_AS",
            codeProperty.GetCollation());
        Assert.Contains(
            entityType.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties.SequenceEqual(
                    new[] { codeProperty }));

        var lifecycleProperty =
            entityType.FindProperty(nameof(Device.Lifecycle));
        Assert.NotNull(lifecycleProperty);
        Assert.Equal(50, lifecycleProperty.GetMaxLength());

        var registeredProperty =
            entityType.FindProperty(nameof(Device.RegisteredAtUtc));
        var retiredProperty =
            entityType.FindProperty(nameof(Device.RetiredAtUtc));
        Assert.NotNull(registeredProperty);
        Assert.NotNull(retiredProperty);
        Assert.False(registeredProperty.IsNullable);
        Assert.True(retiredProperty.IsNullable);
        Assert.Equal("datetime2", registeredProperty.GetColumnType());
        Assert.Equal("datetime2", retiredProperty.GetColumnType());

        Assert.Null(entityType.FindProperty(nameof(Device.Capabilities)));
        var capabilitiesProperty = entityType.FindProperty("_capabilities");
        Assert.NotNull(capabilitiesProperty);
        Assert.Equal("Capabilities", capabilitiesProperty.GetColumnName());
    }

    [Fact]
    public async Task SaveAndReloadPreservesDeviceDataAndCapabilities()
    {
        using var database = new SqliteDeviceManagementDatabase();
        var item = DeviceCatalogTestData.CreateItem();
        var device = DeviceCatalogTestData.CreateDevice(item);

        await using (var writeContext = database.CreateDbContext())
        {
            writeContext.Devices.Add(device);
            await writeContext.SaveChangesAsync();
        }

        await using var readContext = database.CreateDbContext();
        var loadedDevice = await readContext.Devices
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(device.Id, loadedDevice.Id);
        Assert.Equal(device.Code, loadedDevice.Code);
        Assert.Equal(device.Name, loadedDevice.Name);
        Assert.Equal(DeviceLifecycle.Registered, loadedDevice.Lifecycle);
        Assert.Equal(DateTimeKind.Utc, loadedDevice.RegisteredAtUtc.Kind);
        Assert.Equal(2, loadedDevice.Capabilities.Count);
        Assert.Contains(
            DeviceCapability.Temperature,
            loadedDevice.Capabilities);
        Assert.Contains(
            DeviceCapability.Humidity,
            loadedDevice.Capabilities);
    }

    [Fact]
    public async Task UniqueCodeIndexRejectsCaseInsensitiveDuplicate()
    {
        using var database = new SqliteDeviceManagementDatabase();
        await using var context = database.CreateDbContext();
        var firstItem = DeviceCatalogTestData.CreateItem();
        var secondItem = DeviceCatalogTestData.CreateItem(2);
        var firstDevice = DeviceCatalogTestData.CreateDevice(firstItem);
        var secondDevice = new Device(
            firstItem.Code.ToLowerInvariant(),
            secondItem.Name,
            secondItem.HardwareModel,
            secondItem.HardwareRevision,
            secondItem.FirmwareVersion,
            secondItem.Location,
            secondItem.Capabilities,
            DeviceCatalogTestData.RegisteredAtUtc);

        context.Devices.Add(firstDevice);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.Devices.Add(secondDevice);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveAndReloadPreservesRetirementAsUtc()
    {
        using var database = new SqliteDeviceManagementDatabase();
        var device = DeviceCatalogTestData.CreateDevice(
            DeviceCatalogTestData.CreateItem());
        var retiredAtUtc = DeviceCatalogTestData.RegisteredAtUtc.AddDays(1);
        device.Retire(retiredAtUtc);

        await using (var writeContext = database.CreateDbContext())
        {
            writeContext.Devices.Add(device);
            await writeContext.SaveChangesAsync();
        }

        await using var readContext = database.CreateDbContext();
        var loadedDevice = await readContext.Devices
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(DeviceLifecycle.Retired, loadedDevice.Lifecycle);
        Assert.Equal(retiredAtUtc, loadedDevice.RetiredAtUtc);
        Assert.Equal(DateTimeKind.Utc, loadedDevice.RetiredAtUtc?.Kind);
    }

    [Fact]
    public async Task UpdatingCapabilitiesPersistsTheNewSet()
    {
        using var database = new SqliteDeviceManagementDatabase();
        var device = DeviceCatalogTestData.CreateDevice(
            DeviceCatalogTestData.CreateItem());

        await using (var createContext = database.CreateDbContext())
        {
            createContext.Devices.Add(device);
            await createContext.SaveChangesAsync();
        }

        await using (var updateContext = database.CreateDbContext())
        {
            var trackedDevice = await updateContext.Devices.SingleAsync();
            trackedDevice.UpdateMetadata(
                trackedDevice.Name,
                trackedDevice.HardwareModel,
                trackedDevice.HardwareRevision,
                trackedDevice.FirmwareVersion,
                trackedDevice.Location,
                [DeviceCapability.DoorState]);
            await updateContext.SaveChangesAsync();
        }

        await using var readContext = database.CreateDbContext();
        var loadedDevice = await readContext.Devices
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(
            DeviceCapability.DoorState,
            Assert.Single(loadedDevice.Capabilities));
    }
}
