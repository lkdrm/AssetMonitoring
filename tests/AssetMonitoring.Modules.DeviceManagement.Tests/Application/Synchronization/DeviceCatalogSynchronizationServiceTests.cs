using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.Synchronization;

public sealed class DeviceCatalogSynchronizationServiceTests
{
    private static readonly DateTimeOffset SynchronizedAtUtc =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConstructorWithNullLoaderThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogSynchronizationService(
                null!,
                new FakeDeviceRepository(),
                new FixedTimeProvider(SynchronizedAtUtc)));
    }

    [Fact]
    public void ConstructorWithNullRepositoryThrowsArgumentNullException()
    {
        var loader = CreateLoader(
            DeviceCatalogTestData.CreateValidDocument());

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogSynchronizationService(
                loader,
                null!,
                new FixedTimeProvider(SynchronizedAtUtc)));
    }

    [Fact]
    public void ConstructorWithNullTimeProviderThrowsArgumentNullException()
    {
        var loader = CreateLoader(
            DeviceCatalogTestData.CreateValidDocument());

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogSynchronizationService(
                loader,
                new FakeDeviceRepository(),
                null!));
    }

    [Fact]
    public async Task SynchronizeAsyncWithInvalidCatalogDoesNotReadOrSaveDevices()
    {
        var repository = new FakeDeviceRepository();
        var service = CreateService(
            DeviceCatalogTestData.CreateValidDocument(9),
            repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.False(result.HasChanges);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Retired);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, repository.GetAllCallCount);
        Assert.Equal(0, repository.SaveChangesCallCount);
        Assert.Empty(repository.AddedDevices);
    }

    [Fact]
    public async Task SynchronizeAsyncWithNewCatalogCreatesEveryDeviceAndSavesOnce()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var repository = new FakeDeviceRepository();
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.True(result.HasChanges);
        Assert.Equal(10, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Retired);
        Assert.Equal(0, result.Restored);
        Assert.Equal(10, repository.AddedDevices.Count);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.All(
            repository.AddedDevices,
            device => Assert.Equal(
                SynchronizedAtUtc.UtcDateTime,
                device.RegisteredAtUtc));
    }

    [Fact]
    public async Task SynchronizeAsyncWithIdenticalCatalogReportsEveryDeviceUnchanged()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .ToList();
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.False(result.HasChanges);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(10, result.Unchanged);
        Assert.Equal(0, result.Retired);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task SynchronizeAsyncWithChangedMetadataUpdatesDeviceAndSavesOnce()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .ToList();
        existingDevices[0].UpdateMetadata(
            "Old sensor name",
            existingDevices[0].HardwareModel,
            existingDevices[0].HardwareRevision,
            existingDevices[0].FirmwareVersion,
            existingDevices[0].Location,
            existingDevices[0].Capabilities);
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Updated);
        Assert.Equal(9, result.Unchanged);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.Equal(document.Devices[0].Name, existingDevices[0].Name);
    }

    [Fact]
    public async Task SynchronizeAsyncWithMissingDeviceRetiresItAndSavesOnce()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .Append(DeviceCatalogTestData.CreateDevice(
                DeviceCatalogTestData.CreateItem(11)))
            .ToList();
        var missingDevice = existingDevices[^1];
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Retired);
        Assert.Equal(10, result.Unchanged);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.Equal(DeviceLifecycle.Retired, missingDevice.Lifecycle);
        Assert.Equal(SynchronizedAtUtc.UtcDateTime, missingDevice.RetiredAtUtc);
    }

    [Fact]
    public async Task SynchronizeAsyncWithAlreadyRetiredMissingDeviceDoesNotSave()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .Append(DeviceCatalogTestData.CreateDevice(
                DeviceCatalogTestData.CreateItem(11)))
            .ToList();
        existingDevices[^1].Retire(
            DeviceCatalogTestData.RegisteredAtUtc.AddDays(1));
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Retired);
        Assert.Equal(10, result.Unchanged);
        Assert.False(result.HasChanges);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task SynchronizeAsyncWithReturnedRetiredDeviceRestoresIt()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .ToList();
        var restoredDevice = existingDevices[0];
        restoredDevice.Retire(
            DeviceCatalogTestData.RegisteredAtUtc.AddDays(1));
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Restored);
        Assert.Equal(0, result.Updated);
        Assert.Equal(9, result.Unchanged);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.Equal(DeviceLifecycle.Registered, restoredDevice.Lifecycle);
        Assert.Null(restoredDevice.RetiredAtUtc);
    }

    [Fact]
    public async Task SynchronizeAsyncCanRestoreAndUpdateTheSameDevice()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => DeviceCatalogTestData.CreateDevice(item))
            .ToList();
        var changedDevice = existingDevices[0];
        changedDevice.UpdateMetadata(
            "Old sensor name",
            changedDevice.HardwareModel,
            changedDevice.HardwareRevision,
            changedDevice.FirmwareVersion,
            changedDevice.Location,
            changedDevice.Capabilities);
        changedDevice.Retire(
            DeviceCatalogTestData.RegisteredAtUtc.AddDays(1));
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Updated);
        Assert.Equal(9, result.Unchanged);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.Equal(document.Devices[0].Name, changedDevice.Name);
        Assert.Equal(DeviceLifecycle.Registered, changedDevice.Lifecycle);
    }

    [Fact]
    public async Task SynchronizeAsyncMatchesCodesCaseInsensitively()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var existingDevices = document.Devices
            .Select(item => new Device(
                item.Code.ToLowerInvariant(),
                item.Name,
                item.HardwareModel,
                item.HardwareRevision,
                item.FirmwareVersion,
                item.Location,
                item.Capabilities,
                DeviceCatalogTestData.RegisteredAtUtc))
            .ToList();
        var repository = new FakeDeviceRepository(existingDevices);
        var service = CreateService(document, repository);

        var result = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Created);
        Assert.Equal(10, result.Unchanged);
        Assert.Empty(repository.AddedDevices);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task SynchronizeAsyncRepeatedWithSameCatalogIsIdempotent()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var repository = new FakeDeviceRepository();
        var service = CreateService(document, repository);

        var firstResult = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);
        var secondResult = await service.SynchronizeAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Equal(10, firstResult.Created);
        Assert.Equal(10, secondResult.Unchanged);
        Assert.False(secondResult.HasChanges);
        Assert.Equal(10, repository.Devices.Count);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task SynchronizeAsyncForwardsCancellationTokenToPersistence()
    {
        var repository = new FakeDeviceRepository();
        var service = CreateService(
            DeviceCatalogTestData.CreateValidDocument(),
            repository);
        using var cancellationTokenSource = new CancellationTokenSource();

        await service.SynchronizeAsync(
            "catalog.json",
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastGetAllCancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastSaveCancellationToken);
    }

    private static DeviceCatalogSynchronizationService CreateService(
        DeviceCatalogDocument document,
        FakeDeviceRepository repository)
    {
        return new DeviceCatalogSynchronizationService(
            CreateLoader(document),
            repository,
            new FixedTimeProvider(SynchronizedAtUtc));
    }

    private static DeviceCatalogLoader CreateLoader(
        DeviceCatalogDocument document)
    {
        return new DeviceCatalogLoader(
            new DeviceCatalogValidator(),
            new StubDeviceCatalogReader(document));
    }
}
