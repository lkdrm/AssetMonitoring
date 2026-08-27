using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Application.Infrastructure;
using AssetMonitoring.Modules.DeviceManagement.Application.Interfaces;
using AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.Infrastructure;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddDeviceManagementWithNullServicesThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DependencyInjection.AddDeviceManagement(
                null!,
                "Server=(local);Database=AssetMonitoringTests;"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDeviceManagementWithMissingConnectionStringThrowsArgumentException(
        string? connectionString)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddDeviceManagement(connectionString!));
    }

    [Fact]
    public void AddDeviceManagementRegistersExpectedServiceLifetimes()
    {
        var services = new ServiceCollection();

        services.AddDeviceManagement(
            "Server=(local);Database=AssetMonitoringTests;");

        AssertLifetime<IDeviceRepository>(services, ServiceLifetime.Scoped);
        AssertLifetime<IDeviceQueries>(services, ServiceLifetime.Scoped);
        AssertLifetime<DeviceCatalogSynchronizationService>(
            services,
            ServiceLifetime.Scoped);
        AssertLifetime<DeviceManagementDbContext>(
            services,
            ServiceLifetime.Scoped);
        AssertLifetime<IDeviceCatalogReader>(
            services,
            ServiceLifetime.Singleton);
        AssertLifetime<DeviceCatalogValidator>(
            services,
            ServiceLifetime.Singleton);
        AssertLifetime<DeviceCatalogLoader>(
            services,
            ServiceLifetime.Singleton);
        AssertLifetime<TimeProvider>(
            services,
            ServiceLifetime.Singleton);
    }

    private static void AssertLifetime<TService>(
        IServiceCollection services,
        ServiceLifetime expectedLifetime)
    {
        var descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(TService));

        Assert.Equal(expectedLifetime, descriptor.Lifetime);
    }
}
