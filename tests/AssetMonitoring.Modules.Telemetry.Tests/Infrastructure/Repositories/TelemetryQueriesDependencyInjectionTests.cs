using AssetMonitoring.Modules.Telemetry.Application.Interfaces;
using AssetMonitoring.Modules.Telemetry.Infrastructure;
using AssetMonitoring.Modules.Telemetry.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.Telemetry.Tests.Infrastructure.Repositories;

public sealed class TelemetryQueriesDependencyInjectionTests
{
    private const string ConnectionString =
        "Server=(local);Database=AssetMonitoringTelemetryTests;";

    [Fact]
    public void AddTelemetryRegistersTelemetryQueriesAsScoped()
    {
        var services = new ServiceCollection();

        services.AddTelemetry(ConnectionString);

        var descriptor = Assert.Single(
            services,
            service => service.ServiceType == typeof(ITelemetryQueries));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(TelemetryQueries), descriptor.ImplementationType);
    }

    [Fact]
    public void AddTelemetryResolvesSameQueriesWithinScopeAndDifferentAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddTelemetry(ConnectionString);

        using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        ITelemetryQueries firstQueries;

        using (var firstScope = serviceProvider.CreateScope())
        {
            firstQueries = firstScope.ServiceProvider
                .GetRequiredService<ITelemetryQueries>();
            var sameScopeQueries = firstScope.ServiceProvider
                .GetRequiredService<ITelemetryQueries>();

            Assert.Same(firstQueries, sameScopeQueries);
        }

        using var secondScope = serviceProvider.CreateScope();
        var secondQueries = secondScope.ServiceProvider
            .GetRequiredService<ITelemetryQueries>();

        Assert.NotSame(firstQueries, secondQueries);
    }
}
