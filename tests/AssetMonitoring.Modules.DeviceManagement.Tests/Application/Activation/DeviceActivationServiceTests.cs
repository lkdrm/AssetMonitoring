using AssetMonitoring.Modules.DeviceManagement.Application.Activation;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.Activation;

public sealed class DeviceActivationServiceTests
{
    [Fact]
    public void ConstructorWithNullRepositoryThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceActivationService(null!));
    }

    [Fact]
    public async Task ActivateAsyncWhenDeviceDoesNotExistReturnsNull()
    {
        var repository = new FakeDeviceRepository();
        var service = new DeviceActivationService(repository);

        var result = await service.ActivateAsync(Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(1, repository.GetByIdCallCount);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActivateAsyncForRegisteredDeviceActivatesAndSavesOnce()
    {
        var device = CreateDevice();
        var repository = new FakeDeviceRepository([device]);
        var service = new DeviceActivationService(repository);

        var result = await service.ActivateAsync(device.Id);

        Assert.NotNull(result);
        Assert.Equal(device.Id, result.DeviceId);
        Assert.Equal(DeviceLifecycle.Active, result.Lifecycle);
        Assert.True(result.Changed);
        Assert.Equal(DeviceLifecycle.Active, device.Lifecycle);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActivateAsyncForActiveDeviceIsIdempotentAndDoesNotSave()
    {
        var device = CreateDevice();
        device.Activate();
        var repository = new FakeDeviceRepository([device]);
        var service = new DeviceActivationService(repository);

        var result = await service.ActivateAsync(device.Id);

        Assert.NotNull(result);
        Assert.Equal(device.Id, result.DeviceId);
        Assert.Equal(DeviceLifecycle.Active, result.Lifecycle);
        Assert.False(result.Changed);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActivateAsyncForRetiredDevicePropagatesDomainException()
    {
        var device = CreateDevice();
        var retiredAtUtc = DeviceCatalogTestData.RegisteredAtUtc.AddDays(1);
        device.Retire(retiredAtUtc);
        var repository = new FakeDeviceRepository([device]);
        var service = new DeviceActivationService(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ActivateAsync(device.Id));

        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActivateAsyncForwardsCancellationTokenToRepository()
    {
        var device = CreateDevice();
        var repository = new FakeDeviceRepository([device]);
        var service = new DeviceActivationService(repository);
        using var cancellationTokenSource = new CancellationTokenSource();

        await service.ActivateAsync(
            device.Id,
            cancellationTokenSource.Token);

        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastGetByIdCancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            repository.LastSaveCancellationToken);
    }

    private static Device CreateDevice() =>
        DeviceCatalogTestData.CreateDevice(
            DeviceCatalogTestData.CreateItem());
}
