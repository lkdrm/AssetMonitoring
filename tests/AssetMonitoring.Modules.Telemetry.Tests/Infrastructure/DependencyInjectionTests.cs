using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Application.Service;
using AssetMonitoring.Modules.Telemetry.Database;
using AssetMonitoring.Modules.Telemetry.Infrastructure;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddTelemetryWithNullServicesThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DependencyInjection.AddTelemetry(
                null!,
                "Server=(local);Database=AssetMonitoringTests;"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTelemetryWithMissingConnectionStringThrowsArgumentException(
        string? connectionString)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddTelemetry(connectionString!));
    }

    [Fact]
    public void AddTelemetryRegistersDbContextAsScoped()
    {
        var services = new ServiceCollection();

        services.AddTelemetry(
            "Server=(local);Database=AssetMonitoringTests;");

        var descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(TelemetryDbContext));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddTelemetryRegistersMeasurementRepositoryAsScoped()
    {
        var services = new ServiceCollection();

        services.AddTelemetry(
            "Server=(local);Database=AssetMonitoringTests;");

        var descriptor = Assert.Single(
            services,
            item =>
                item.ServiceType ==
                typeof(ITelemetryMeasurementRepository));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(
            typeof(TelemetryMeasurementRepository),
            descriptor.ImplementationType);
    }

    [Fact]
    public void AddTelemetryRegistersRecordingServiceAsScoped()
    {
        var services = new ServiceCollection();

        services.AddTelemetry(
            "Server=(local);Database=AssetMonitoringTests;");

        var descriptor = Assert.Single(
            services,
            item =>
                item.ServiceType ==
                typeof(TelemetryRecordingService));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(
            typeof(TelemetryRecordingService),
            descriptor.ImplementationType);
    }

    [Fact]
    public void AddTelemetryRegistrationsCanBeResolvedWithinScope()
    {
        var services = new ServiceCollection();

        services.AddTelemetry(
            "Server=(local);Database=AssetMonitoringTests;");

        using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        using var scope = serviceProvider.CreateScope();

        var repository = scope.ServiceProvider
            .GetRequiredService<ITelemetryMeasurementRepository>();

        var recordingService = scope.ServiceProvider
            .GetRequiredService<TelemetryRecordingService>();

        Assert.NotNull(repository);
        Assert.NotNull(recordingService);

        Assert.Same(
            repository,
            scope.ServiceProvider
                .GetRequiredService<ITelemetryMeasurementRepository>());

        Assert.Same(
            recordingService,
            scope.ServiceProvider
                .GetRequiredService<TelemetryRecordingService>());
    }
}
