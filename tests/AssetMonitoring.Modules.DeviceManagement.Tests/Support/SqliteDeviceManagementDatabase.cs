using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Support;

internal sealed class SqliteDeviceManagementDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    internal SqliteDeviceManagementDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _connection.CreateCollation(
            "Latin1_General_100_CI_AS",
            (left, right) => string.Compare(
                left,
                right,
                StringComparison.OrdinalIgnoreCase));

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    internal SqliteConnection Connection => _connection;

    internal DeviceManagementDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DeviceManagementDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new DeviceManagementDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}
