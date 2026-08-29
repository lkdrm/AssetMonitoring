using System.Data.Common;
using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Api;

internal sealed class AssetMonitoringApiFactory : WebApplicationFactory<Program>
{
    private readonly TimeProvider? _timeProvider;

    internal AssetMonitoringApiFactory(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(AppContext.BaseDirectory);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DeviceManagement"] =
                        "Server=(local);Database=AssetMonitoringTests;",
                    ["DeviceManagement:Connectivity:OfflineThreshold"] =
                        "00:05:00"
                });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<
                IDbContextOptionsConfiguration<DeviceManagementDbContext>>();
            services.RemoveAll<DbConnection>();

            if (_timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_timeProvider);
            }

            services.AddSingleton<DbConnection>(_ =>
            {
                var connection = new SqliteConnection(
                    "Data Source=:memory:");
                connection.Open();
                connection.CreateCollation(
                    "Latin1_General_100_CI_AS",
                    (left, right) => string.Compare(
                        left,
                        right,
                        StringComparison.OrdinalIgnoreCase));

                return connection;
            });

            services.AddDbContext<DeviceManagementDbContext>(
                (serviceProvider, options) =>
                {
                    var connection = serviceProvider
                        .GetRequiredService<DbConnection>();
                    options.UseSqlite(connection);
                });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var dbContext =
            scope.ServiceProvider.GetRequiredService<DeviceManagementDbContext>();
        dbContext.Database.EnsureCreated();

        return host;
    }
}
