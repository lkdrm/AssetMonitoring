using AssetMonitoring.DeviceSimulator.Configuration;
using AssetMonitoring.DeviceSimulator.Configuration.Validation;
using AssetMonitoring.DeviceSimulator.Infrastructure;
using AssetMonitoring.DeviceSimulator.Interfaces;
using AssetMonitoring.DeviceSimulator.Load;
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

    /// <summary>
    /// Builds a validated provider with the environment normally supplied by the host.
    /// </summary>
    /// <returns>A service provider that must be disposed by the caller.</returns>
    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddDeviceSimulator();

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
