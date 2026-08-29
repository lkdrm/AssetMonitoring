using System.Net;
using System.Text;
using System.Text.Json;
using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Api;

public sealed class DeviceHeartbeatApiTests
{
    [Fact]
    public async Task RecordHeartbeatForActiveDeviceReturnsAndPersistsTimestamp()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);
        using var activationResponse = await ActivateAsync(client, deviceId);
        activationResponse.EnsureSuccessStatusCode();

        using var response = await RecordHeartbeatAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;
        var lastHeartbeatAtUtc =
            result.GetProperty("lastHeartbeatAtUtc").GetDateTime();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(deviceId, result.GetProperty("deviceId").GetGuid());
        Assert.Equal(DateTimeKind.Utc, lastHeartbeatAtUtc.Kind);
        Assert.True(result.GetProperty("changed").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<DeviceManagementDbContext>();
        var persistedHeartbeatAtUtc = await dbContext.Devices
            .AsNoTracking()
            .Where(device => device.Id == deviceId)
            .Select(device => device.LastHeartbeatAtUtc)
            .SingleAsync();

        Assert.Equal(lastHeartbeatAtUtc, persistedHeartbeatAtUtc);
        Assert.Equal(DateTimeKind.Utc, persistedHeartbeatAtUtc?.Kind);
    }

    [Fact]
    public async Task RecordHeartbeatForMissingDeviceReturnsNotFoundProblemDetails()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = Guid.NewGuid();

        using var response = await RecordHeartbeatAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Device not found.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            $"A device with ID '{deviceId}' was not found.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(
            $"/api/devices/{deviceId}/heartbeat",
            problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task RecordHeartbeatForRegisteredDeviceReturnsConflictProblemDetails()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);

        using var response = await RecordHeartbeatAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        AssertHeartbeatConflict(problem, response.StatusCode, deviceId);
    }

    [Fact]
    public async Task RecordHeartbeatForRetiredDeviceReturnsConflictProblemDetails()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<DeviceManagementDbContext>();
            var device = await dbContext.Devices.SingleAsync(
                item => item.Id == deviceId);
            device.Retire(
                new DateTime(
                    2026,
                    8,
                    28,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc));
            await dbContext.SaveChangesAsync();
        }

        using var response = await RecordHeartbeatAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        AssertHeartbeatConflict(problem, response.StatusCode, deviceId);
    }

    private static void AssertHeartbeatConflict(
        JsonElement problem,
        HttpStatusCode statusCode,
        Guid deviceId)
    {
        Assert.Equal(HttpStatusCode.Conflict, statusCode);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Device heartbeat rejected.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            "Only an active device can record a heartbeat.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(
            $"/api/devices/{deviceId}/heartbeat",
            problem.GetProperty("instance").GetString());
    }

    private static HttpClient CreateClient(AssetMonitoringApiFactory factory) =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

    private static Task<HttpResponseMessage> ActivateAsync(
        HttpClient client,
        Guid deviceId) =>
        client.PostAsync(
            $"/api/devices/{deviceId}/activate",
            new StringContent(string.Empty, Encoding.UTF8));

    private static Task<HttpResponseMessage> RecordHeartbeatAsync(
        HttpClient client,
        Guid deviceId) =>
        client.PostAsync(
            $"/api/devices/{deviceId}/heartbeat",
            new StringContent(string.Empty, Encoding.UTF8));

    private static async Task<Guid> SynchronizeAndGetDeviceIdAsync(
        AssetMonitoringApiFactory factory,
        HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/device-catalog/synchronize",
            new StringContent(string.Empty, Encoding.UTF8));
        response.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<DeviceManagementDbContext>();
        return await dbContext.Devices
            .Where(device => device.Code == "WH-001")
            .Select(device => device.Id)
            .SingleAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
