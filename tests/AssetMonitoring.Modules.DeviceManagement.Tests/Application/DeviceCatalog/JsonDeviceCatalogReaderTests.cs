using System.Text.Json;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.DeviceCatalog;

public sealed class JsonDeviceCatalogReaderTests
{
    private readonly JsonDeviceCatalogReader _reader = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsyncWithMissingPathThrowsArgumentException(
        string? path)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _reader.ReadAsync(path!));
    }

    [Fact]
    public async Task ReadAsyncWithValidJsonDeserializesStringEnums()
    {
        const string json = """
            {
              "DEVICES": [
                {
                  "CODE": "WH-001",
                  "NAME": "Warehouse sensor",
                  "HARDWAREMODEL": "Sensor-X",
                  "HARDWAREREVISION": "R1",
                  "FIRMWAREVERSION": "1.0.0",
                  "LOCATION": "Warehouse A",
                  "CAPABILITIES": ["Temperature", "Humidity"]
                }
              ]
            }
            """;
        var path = await WriteTemporaryFileAsync(json);

        try
        {
            var document = await _reader.ReadAsync(path);

            var device = Assert.Single(document.Devices);
            Assert.Equal("WH-001", device.Code);
            Assert.Equal(
                new[]
                {
                    DeviceCapability.Temperature,
                    DeviceCapability.Humidity
                },
                device.Capabilities);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsyncWithNumericEnumThrowsJsonException()
    {
        const string json = """
            {
              "devices": [
                {
                  "code": "WH-001",
                  "name": "Warehouse sensor",
                  "hardwareModel": "Sensor-X",
                  "hardwareRevision": "R1",
                  "firmwareVersion": "1.0.0",
                  "location": "Warehouse A",
                  "capabilities": [0]
                }
              ]
            }
            """;
        var path = await WriteTemporaryFileAsync(json);

        try
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                _reader.ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ invalid json }")]
    public async Task ReadAsyncWithInvalidDocumentThrowsJsonException(
        string json)
    {
        var path = await WriteTemporaryFileAsync(json);

        try
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                _reader.ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsyncWithMissingFileThrowsFileNotFoundException()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"missing-{Guid.NewGuid():N}.json");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _reader.ReadAsync(path));
    }

    [Fact]
    public async Task ReadAsyncWithCanceledTokenThrowsOperationCanceledException()
    {
        const string json = "{ \"devices\": [] }";
        var path = await WriteTemporaryFileAsync(json);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _reader.ReadAsync(
                    path,
                    cancellationTokenSource.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteTemporaryFileAsync(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"device-catalog-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, content);

        return path;
    }
}
