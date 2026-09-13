using AssetMonitoring.Modules.DeviceManagement.Application.Heartbeat;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.Heartbeat;

public sealed class DeviceHeartbeatServiceTests
{
    private static readonly DateTime RegisteredAtUtc =
        new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime HeartbeatAtUtc =
        RegisteredAtUtc.AddMinutes(10);

    [Fact]
    public void ConstructorWithNullRepositoryThrowsArgumentNullException()
    {
        var timeProvider = CreateTimeProvider();

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceHeartbeatService(null!, timeProvider));
    }

    [Fact]
    public void ConstructorWithNullTimeProviderThrowsArgumentNullException()
    {
        var repository = new FakeDeviceRepository();

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceHeartbeatService(repository, null!));
    }

    [Fact]
    public async Task RecordAsyncForMissingDeviceReturnsNullWithoutSaving()
    {
        var repository = new FakeDeviceRepository();
        var service = CreateService(repository);

        var result = await service.RecordAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, repository.GetByIdCallCount);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncForActiveDeviceRecordsServerTimeAndSavesOnce()
    {
        var device = CreateDevice();
        device.Activate();
        var repository = new FakeDeviceRepository([device]);
        var service = CreateService(repository);

        var result = await service.RecordAsync(device.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(device.Id, result.DeviceId);
        Assert.Equal(HeartbeatAtUtc, result.LastHeartbeatAtUtc);
        Assert.True(result.Changed);
        Assert.Equal(HeartbeatAtUtc, device.LastHeartbeatAtUtc);
        Assert.Equal(1, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncWithUnchangedHeartbeatReturnsFalseWithoutSaving()
    {
        var device = CreateDevice();
        device.Activate();
        device.RecordHeartbeat(HeartbeatAtUtc);
        var repository = new FakeDeviceRepository([device]);
        var service = CreateService(repository);

        var result = await service.RecordAsync(device.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(HeartbeatAtUtc, result.LastHeartbeatAtUtc);
        Assert.False(result.Changed);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncForRegisteredDeviceThrowsWithoutSaving()
    {
        var device = CreateDevice();
        var repository = new FakeDeviceRepository([device]);
        var service = CreateService(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordAsync(device.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DeviceLifecycle.Registered, device.Lifecycle);
        Assert.Null(device.LastHeartbeatAtUtc);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncForRetiredDeviceThrowsWithoutSaving()
    {
        var device = CreateDevice();
        var retiredAtUtc = RegisteredAtUtc.AddMinutes(1);
        device.Retire(retiredAtUtc);
        var repository = new FakeDeviceRepository([device]);
        var service = CreateService(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordAsync(device.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DeviceLifecycle.Retired, device.Lifecycle);
        Assert.Equal(retiredAtUtc, device.RetiredAtUtc);
        Assert.Null(device.LastHeartbeatAtUtc);
        Assert.Equal(0, repository.SaveChangesCallCount);
    }

    [Fact]
    public async Task RecordAsyncForwardsCancellationToken()
    {
        var device = CreateDevice();
        device.Activate();
        var repository = new FakeDeviceRepository([device]);
        var service = CreateService(repository);
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        await service.RecordAsync(device.Id, cancellationToken);

        Assert.Equal(
            cancellationToken,
            repository.LastGetByIdCancellationToken);
        Assert.Equal(
            cancellationToken,
            repository.LastSaveCancellationToken);
    }

    private static DeviceHeartbeatService CreateService(
        FakeDeviceRepository repository)
    {
        return new DeviceHeartbeatService(
            repository,
            CreateTimeProvider());
    }

    private static TimeProvider CreateTimeProvider()
    {
        return new FixedTimeProvider(new DateTimeOffset(HeartbeatAtUtc));
    }

    private static Device CreateDevice()
    {
        return new Device(
            "WH-001",
            "Warehouse sensor",
            "Sensor-X",
            "R1",
            "1.0.0",
            "Warehouse A",
            [DeviceCapability.Temperature],
            RegisteredAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
