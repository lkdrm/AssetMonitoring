using AssetMonitoring.Modules.DeviceManagement.Application.Database;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Api;

public sealed class DeviceActivationApiTests
{
    [Fact]
    public async Task ActivateRegisteredDeviceReturnsChangedActiveResult()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);

        using var response = await ActivateAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(deviceId, result.GetProperty("deviceId").GetGuid());
        Assert.Equal(
            DeviceLifecycle.Active.ToString(),
            result.GetProperty("lifecycle").GetString());
        Assert.True(result.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task ActivateActiveDeviceReturnsUnchangedActiveResult()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);
        using var firstResponse = await ActivateAsync(client, deviceId);
        firstResponse.EnsureSuccessStatusCode();

        using var secondResponse = await ActivateAsync(client, deviceId);
        using var document = await ReadJsonAsync(secondResponse);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(deviceId, result.GetProperty("deviceId").GetGuid());
        Assert.Equal(
            DeviceLifecycle.Active.ToString(),
            result.GetProperty("lifecycle").GetString());
        Assert.False(result.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task ActivateMissingDeviceReturnsNotFoundProblemDetails()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = Guid.NewGuid();

        using var response = await ActivateAsync(client, deviceId);
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
            $"/api/devices/{deviceId}/activate",
            problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task ActivateRetiredDeviceReturnsConflictProblemDetails()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);
        var deviceId = await SynchronizeAndGetDeviceIdAsync(factory, client);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<DeviceManagementDbContext>();
            var device = await dbContext.Devices.SingleAsync(item => item.Id == deviceId, cancellationToken: TestContext.Current.CancellationToken);
            device.Retire(
                new DateTime(
                    2026,
                    8,
                    27,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc));
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var response = await ActivateAsync(client, deviceId);
        using var document = await ReadJsonAsync(response);
        var problem = document.RootElement;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(
            "Device cannot be activated.",
            problem.GetProperty("title").GetString());
        Assert.Equal(
            "A retired device must be restored before activation.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(
            $"/api/devices/{deviceId}/activate",
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
