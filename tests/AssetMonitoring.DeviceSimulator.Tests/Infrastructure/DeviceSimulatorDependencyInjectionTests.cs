using AssetMonitoring.DeviceSimulator.Api;
using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Infrastructure;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
using AssetMonitoring.DeviceSimulator.Preparation;
using AssetMonitoring.DeviceSimulator.Runtime;
using AssetMonitoring.DeviceSimulator.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AssetMonitoring.DeviceSimulator.Tests.Infrastructure;

/// <summary>
/// Verifies registration and singleton lifetimes of device simulator services.
/// </summary>
public sealed class DeviceSimulatorDependencyInjectionTests
{
    /// <summary>
    /// Verifies that a service collection is required for registration.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorWithNullServicesThrowsArgumentNullException()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.AddDeviceSimulator());

        Assert.Equal("services", exception.ParamName);
    }

    /// <summary>
    /// Verifies that registration returns the supplied collection for chaining.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddDeviceSimulator();

        Assert.Same(services, result);
    }

    /// <summary>
    /// Verifies that all plan services can be constructed when the host
    /// environment is available.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorResolvesPlanServicesWithHostEnvironment()
    {
        using var provider = CreateServiceProvider();

        var reader = provider.GetRequiredService<ISimulationPlanReader>();
        var validator = provider.GetRequiredService<SimulationPlanValidator>();
        var loader = provider.GetRequiredService<SimulationPlanLoader>();

        Assert.IsType<JsonSimulationPlanReader>(reader);
        Assert.IsType<SimulationPlanValidator>(validator);
        Assert.IsType<SimulationPlanLoader>(loader);
    }

    /// <summary>
    /// Verifies that the reader instance is shared across resolutions and scopes.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorSharesReaderAcrossScopes()
    {
        AssertSingletonAcrossScopes<ISimulationPlanReader>();
    }

    /// <summary>
    /// Verifies that the validator instance is shared across resolutions and scopes.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorSharesValidatorAcrossScopes()
    {
        AssertSingletonAcrossScopes<SimulationPlanValidator>();
    }

    /// <summary>
    /// Verifies that the loader instance is shared across resolutions and scopes.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorSharesLoaderAcrossScopes()
    {
        AssertSingletonAcrossScopes<SimulationPlanLoader>();
    }

    /// <summary>Verifies that preparation and runtime services can be constructed together.</summary>
    [Fact]
    public void AddDeviceSimulatorResolvesPreparationAndRuntimeServices()
    {
        using var provider = CreateServiceProvider();

        Assert.IsType<DeviceSimulatorApiClient>(provider.GetRequiredService<IDeviceSimulatorApiClient>());
        Assert.IsType<DevicePreparationService>(provider.GetRequiredService<DevicePreparationService>());
        Assert.IsType<ScenarioTargetResolver>(provider.GetRequiredService<ScenarioTargetResolver>());
        Assert.IsType<SimulationPlanResolver>(provider.GetRequiredService<SimulationPlanResolver>());
        Assert.IsType<DeviceTemperatureScenarioSequenceFactory>(
            provider.GetRequiredService<DeviceTemperatureScenarioSequenceFactory>());
        Assert.IsType<NormalTelemetryGenerator>(provider.GetRequiredService<NormalTelemetryGenerator>());
        Assert.IsType<DeviceHeartbeatRunner>(provider.GetRequiredService<DeviceHeartbeatRunner>());
        Assert.IsType<DeviceHeartbeatCoordinator>(provider.GetRequiredService<DeviceHeartbeatCoordinator>());
        Assert.IsType<DeviceTelemetryRunner>(provider.GetRequiredService<DeviceTelemetryRunner>());
        Assert.IsType<DeviceTelemetryCoordinator>(provider.GetRequiredService<DeviceTelemetryCoordinator>());
    }

    /// <summary>Verifies that the stateless sequence factory is shared across resolutions and scopes.</summary>
    [Fact]
    public void AddDeviceSimulatorSharesSequenceFactoryAcrossScopes()
    {
        AssertSingletonAcrossScopes<DeviceTemperatureScenarioSequenceFactory>();
    }

    /// <summary>Verifies that worker and runner dependencies resolve one shared time provider.</summary>
    [Fact]
    public void AddDeviceSimulatorSharesTimeProviderAcrossScopes()
    {
        AssertSingletonAcrossScopes<TimeProvider>();
    }

    /// <summary>
    /// Verifies construction of the hosted worker with its sequence factory,
    /// shared time provider, and runtime coordinators without starting simulation.
    /// </summary>
    [Fact]
    public void AddDeviceSimulatorResolvesHostedWorkerWithScenarioDependencies()
    {
        using var provider = CreateServiceProvider();

        var service = Assert.Single(provider.GetServices<IHostedService>());

        Assert.IsType<SimulationWorker>(service);
    }

    /// <summary>
    /// Builds a validated provider with the environment normally supplied by the host.
    /// </summary>
    /// <returns>A service provider that must be disposed by the caller.</returns>
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<IHostApplicationLifetime>(new TestApplicationLifetime());
        services.Configure<DeviceSimulatorOptions>(options =>
        {
            options.PlanName = "normal-operation";
            options.ApiBaseAddress = "https://localhost:7056/";
            options.WarehouseTimeZoneId = "UTC";
        });
        services.AddDeviceSimulator();
        services.AddHostedService<SimulationWorker>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>
    /// Verifies that root and scoped resolutions return the same service instance.
    /// </summary>
    /// <typeparam name="TService">The registered service type to resolve.</typeparam>
    private static void AssertSingletonAcrossScopes<TService>()
        where TService : class
    {
        using var provider = CreateServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var instance = provider.GetRequiredService<TService>();

        Assert.Same(instance, provider.GetRequiredService<TService>());
        Assert.Same(instance, firstScope.ServiceProvider.GetRequiredService<TService>());
        Assert.Same(instance, secondScope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Supplies the host lifetime dependency without starting a real host.</summary>
    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        /// <inheritdoc />
        public CancellationToken ApplicationStarted => CancellationToken.None;

        /// <inheritdoc />
        public CancellationToken ApplicationStopping => CancellationToken.None;

        /// <inheritdoc />
        public CancellationToken ApplicationStopped => CancellationToken.None;

        /// <inheritdoc />
        public void StopApplication()
        {
        }
    }

    /// <summary>
    /// Supplies hosting metadata needed to construct the JSON plan reader
    /// without starting a host or reading plan files.
    /// </summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <inheritdoc />
        public string EnvironmentName { get; set; } = Environments.Development;

        /// <inheritdoc />
        public string ApplicationName { get; set; } =
            nameof(DeviceSimulatorDependencyInjectionTests);

        /// <inheritdoc />
        public string ContentRootPath { get; set; } = Path.GetTempPath();

        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
