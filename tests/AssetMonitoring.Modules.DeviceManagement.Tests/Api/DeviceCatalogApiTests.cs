using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Api;

public sealed class DeviceCatalogApiTests
{
    [Fact]
    public async Task ValidationReturnsTheConfiguredCatalogAndValidResult()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/device-catalog/validation");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("isValid").GetBoolean());
        Assert.Empty(
            document.RootElement
                .GetProperty("validationResult")
                .GetProperty("errors")
                .EnumerateArray());
        Assert.Equal(
            10,
            document.RootElement
                .GetProperty("document")
                .GetProperty("devices")
                .GetArrayLength());
    }

    [Fact]
    public async Task FirstSynchronizationCreatesEveryCatalogDevice()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);

        using var response = await SynchronizeAsync(client);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.True(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(10, root.GetProperty("created").GetInt32());
        Assert.Equal(0, root.GetProperty("updated").GetInt32());
        Assert.Equal(0, root.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, root.GetProperty("retired").GetInt32());
        Assert.Equal(0, root.GetProperty("restored").GetInt32());
    }

    [Fact]
    public async Task RepeatedSynchronizationIsIdempotent()
    {
        using var factory = new AssetMonitoringApiFactory();
        using var client = CreateClient(factory);

        using var firstResponse = await SynchronizeAsync(client);
        firstResponse.EnsureSuccessStatusCode();

        using var secondResponse = await SynchronizeAsync(client);
        using var document = await ReadJsonAsync(secondResponse);
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.False(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(0, root.GetProperty("created").GetInt32());
        Assert.Equal(0, root.GetProperty("updated").GetInt32());
        Assert.Equal(10, root.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, root.GetProperty("retired").GetInt32());
        Assert.Equal(0, root.GetProperty("restored").GetInt32());
    }

    private static HttpClient CreateClient(AssetMonitoringApiFactory factory) =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

    private static Task<HttpResponseMessage> SynchronizeAsync(
        HttpClient client) =>
        client.PostAsync(
            "/api/device-catalog/synchronize",
            new StringContent(string.Empty, Encoding.UTF8));

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
