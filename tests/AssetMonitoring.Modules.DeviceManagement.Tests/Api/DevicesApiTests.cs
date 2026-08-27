using System.Net;
using System.Text;
using System.Text.Json;
using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Api;

public sealed class DevicesApiTests
{
    [Fact]
    public async Task GetAllReturnsEveryDeviceOrderedByCode()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        await SynchronizeAsync(client);

        using var response = await client.GetAsync("/api/devices");
        using var document = await ReadJsonAsync(response);
        var devices = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, devices.Length);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(number => $"WH-{number:000}"),
            devices.Select(
                device => device.GetProperty("code").GetString()!));
        Assert.All(
            devices,
            device => Assert.Equal(
                "Registered",
                device.GetProperty("lifecycle").GetString()));
    }

    [Fact]
    public async Task GetByCodeReturnsTheRequestedDeviceContract()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        await SynchronizeAsync(client);

        using var response = await client.GetAsync("/api/devices/WH-001");
        using var document = await ReadJsonAsync(response);
        var device = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("WH-001", device.GetProperty("code").GetString());
        Assert.Equal(
            "North Temperature Sensor",
            device.GetProperty("name").GetString());
        Assert.Equal(
            new[] { "Temperature", "Humidity" },
            device.GetProperty("capabilities")
                .EnumerateArray()
                .Select(capability => capability.GetString()!));
        Assert.Equal(
            "Registered",
            device.GetProperty("lifecycle").GetString());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("retiredAtUtc").ValueKind);
    }

    [Fact]
    public async Task GetByCodeReturnsProblemDetailsWhenDeviceDoesNotExist()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        await SynchronizeAsync(client);

        using var response = await client.GetAsync("/api/devices/UNKNOWN");
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Device not found.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            "A device with code 'UNKNOWN' was not found.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(
            "/api/devices/UNKNOWN",
            problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task GetAllFiltersDevicesByLifecycle()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        await SynchronizeAsync(client);
        var retiredAtUtc = new DateTime(
            2026,
            8,
            27,
            12,
            0,
            0,
            DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<DeviceManagementDbContext>();
            var device = await dbContext.Devices.SingleAsync(
                item => item.Code == "WH-001");
            device.Retire(retiredAtUtc);
            await dbContext.SaveChangesAsync();
        }

        using var response = await client.GetAsync(
            "/api/devices?lifecycle=Retired");
        using var document = await ReadJsonAsync(response);
        var deviceResponse = Assert.Single(
            document.RootElement.EnumerateArray().ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("WH-001", deviceResponse.GetProperty("code").GetString());
        Assert.Equal(
            DeviceLifecycle.Retired.ToString(),
            deviceResponse.GetProperty("lifecycle").GetString());
        Assert.Equal(
            retiredAtUtc,
            deviceResponse.GetProperty("retiredAtUtc").GetDateTime());
    }

    [Fact]
    public async Task GetAllReturnsBadRequestForUnknownLifecycle()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(
            "/api/devices?lifecycle=Unknown");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            400,
            document.RootElement.GetProperty("status").GetInt32());
    }

    private static HttpClient CreateClient(AssetMonitoringApiFactory factory) =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

    private static async Task SynchronizeAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/device-catalog/synchronize",
            new StringContent(string.Empty, Encoding.UTF8));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
