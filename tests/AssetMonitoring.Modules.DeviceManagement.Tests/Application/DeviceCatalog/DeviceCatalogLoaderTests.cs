using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.DeviceCatalog;

public sealed class DeviceCatalogLoaderTests
{
    [Fact]
    public void ConstructorWithNullValidatorThrowsArgumentNullException()
    {
        var reader = new StubDeviceCatalogReader(
            DeviceCatalogTestData.CreateValidDocument());

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogLoader(null!, reader));
    }

    [Fact]
    public void ConstructorWithNullReaderThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogLoader(new DeviceCatalogValidator(), null!));
    }

    [Fact]
    public async Task LoadAsyncWithValidDocumentReturnsValidResult()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var reader = new StubDeviceCatalogReader(document);
        var loader = new DeviceCatalogLoader(
            new DeviceCatalogValidator(),
            reader);

        var result = await loader.LoadAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.Same(document, result.Document);
        Assert.True(result.IsValid);
        Assert.Empty(result.ValidationResult.Errors);
    }

    [Fact]
    public async Task LoadAsyncWithInvalidDocumentReturnsValidationErrors()
    {
        var reader = new StubDeviceCatalogReader(
            DeviceCatalogTestData.CreateValidDocument(9));
        var loader = new DeviceCatalogLoader(
            new DeviceCatalogValidator(),
            reader);

        var result = await loader.LoadAsync("catalog.json", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.ValidationResult.Errors,
            error => error.ErrorCode == "CatalogDeviceCount");
    }

    [Fact]
    public async Task LoadAsyncForwardsPathAndCancellationToken()
    {
        var reader = new StubDeviceCatalogReader(
            DeviceCatalogTestData.CreateValidDocument());
        var loader = new DeviceCatalogLoader(
            new DeviceCatalogValidator(),
            reader);
        using var cancellationTokenSource = new CancellationTokenSource();

        await loader.LoadAsync(
            "Configuration/device-catalog.json",
            cancellationTokenSource.Token);

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(
            "Configuration/device-catalog.json",
            reader.LastPath);
        Assert.Equal(
            cancellationTokenSource.Token,
            reader.LastCancellationToken);
    }

    [Fact]
    public async Task LoadAsyncWhenReaderFailsPropagatesException()
    {
        var expectedException = new IOException("Catalog read failed.");
        var reader = new StubDeviceCatalogReader(
            (_, _) => Task.FromException<
                AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.DeviceCatalogDocument>(
                    expectedException));
        var loader = new DeviceCatalogLoader(
            new DeviceCatalogValidator(),
            reader);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            loader.LoadAsync("catalog.json", TestContext.Current.CancellationToken));

        Assert.Same(expectedException, exception);
    }
}
