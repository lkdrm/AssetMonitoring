using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog;
using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using AssetMonitoring.Modules.DeviceManagement.Tests.Support;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.DeviceCatalog.Validation;

public sealed class DeviceCatalogValidatorTests
{
    private readonly DeviceCatalogValidator _validator = new();

    [Fact]
    public void ValidateWithNullDocumentThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null!));
    }

    [Fact]
    public void ValidateWithValidCatalogReturnsValidResult()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();

        var result = _validator.Validate(document);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    public void ValidateWithIncorrectDeviceCountReturnsCatalogError(
        int deviceCount)
    {
        var document = DeviceCatalogTestData.CreateValidDocument(deviceCount);

        var result = _validator.Validate(document);

        var error = Assert.Single(
            result.Errors,
            item => item.ErrorCode == "CatalogDeviceCount");
        Assert.Equal(nameof(document.Devices), error.PropertyName);
        Assert.Contains(deviceCount.ToString(), error.Message);
    }

    [Theory]
    [InlineData(nameof(DeviceCatalogItem.Code))]
    [InlineData(nameof(DeviceCatalogItem.Name))]
    [InlineData(nameof(DeviceCatalogItem.HardwareModel))]
    [InlineData(nameof(DeviceCatalogItem.HardwareRevision))]
    [InlineData(nameof(DeviceCatalogItem.FirmwareVersion))]
    [InlineData(nameof(DeviceCatalogItem.Location))]
    public void ValidateWithMissingRequiredFieldReturnsFieldError(
        string propertyName)
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        document.Devices[0] = CreateItemWithMissingField(propertyName);

        var result = _validator.Validate(document);

        var error = Assert.Single(
            result.Errors,
            item => item.ErrorCode == "RequiredFieldMissing");
        Assert.Equal(propertyName, error.PropertyName);
        Assert.Equal(
            propertyName == nameof(DeviceCatalogItem.Code)
                ? string.Empty
                : "WH-001",
            error.DeviceCode);
    }

    [Fact]
    public void ValidateWithCaseInsensitiveDuplicateCodeReturnsDuplicateError()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        document.Devices[1] = CopyItem(
            document.Devices[1],
            code: "wh-001");

        var result = _validator.Validate(document);

        var error = Assert.Single(
            result.Errors,
            item => item.ErrorCode == "DuplicateDeviceCode");
        Assert.Equal("wh-001", error.DeviceCode);
        Assert.Equal(nameof(DeviceCatalogItem.Code), error.PropertyName);
    }

    [Fact]
    public void ValidateWithEmptyCapabilitiesReturnsCapabilitiesMissingError()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        document.Devices[0] = CopyItem(
            document.Devices[0],
            capabilities: []);

        var result = _validator.Validate(document);

        var error = Assert.Single(
            result.Errors,
            item => item.ErrorCode == "DeviceCapabilitiesMissing");
        Assert.Equal("WH-001", error.DeviceCode);
        Assert.Equal(nameof(DeviceCatalogItem.Capabilities), error.PropertyName);
    }

    [Fact]
    public void ValidateWithNullCapabilitiesReturnsCapabilitiesMissingError()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        var source = document.Devices[0];
        document.Devices[0] = new DeviceCatalogItem
        {
            Code = source.Code,
            Name = source.Name,
            HardwareModel = source.HardwareModel,
            HardwareRevision = source.HardwareRevision,
            FirmwareVersion = source.FirmwareVersion,
            Location = source.Location,
            Capabilities = null!
        };

        var result = _validator.Validate(document);

        Assert.Contains(
            result.Errors,
            item => item.ErrorCode == "DeviceCapabilitiesMissing");
    }

    [Fact]
    public void ValidateWithDuplicateCapabilityReturnsDuplicateError()
    {
        var document = DeviceCatalogTestData.CreateValidDocument();
        document.Devices[0] = CopyItem(
            document.Devices[0],
            capabilities:
            [
                DeviceCapability.Temperature,
                DeviceCapability.Temperature
            ]);

        var result = _validator.Validate(document);

        var error = Assert.Single(
            result.Errors,
            item => item.ErrorCode == "DuplicateCapability");
        Assert.Equal("WH-001", error.DeviceCode);
        Assert.Equal(nameof(DeviceCatalogItem.Capabilities), error.PropertyName);
    }

    [Fact]
    public void ValidateWithSeveralViolationsCollectsEveryError()
    {
        var document = DeviceCatalogTestData.CreateValidDocument(9);
        document.Devices[0] = CopyItem(
            document.Devices[0],
            name: string.Empty,
            capabilities:
            [
                DeviceCapability.Temperature,
                DeviceCapability.Temperature
            ]);
        document.Devices[1] = CopyItem(
            document.Devices[1],
            code: "wh-001");

        var result = _validator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == "CatalogDeviceCount");
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == "RequiredFieldMissing");
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == "DuplicateCapability");
        Assert.Contains(
            result.Errors,
            error => error.ErrorCode == "DuplicateDeviceCode");
    }

    private static DeviceCatalogItem CreateItemWithMissingField(
        string propertyName)
    {
        var source = DeviceCatalogTestData.CreateItem();

        return new DeviceCatalogItem
        {
            Code = propertyName == nameof(DeviceCatalogItem.Code)
                ? string.Empty
                : source.Code,
            Name = propertyName == nameof(DeviceCatalogItem.Name)
                ? string.Empty
                : source.Name,
            HardwareModel =
                propertyName == nameof(DeviceCatalogItem.HardwareModel)
                    ? string.Empty
                    : source.HardwareModel,
            HardwareRevision =
                propertyName == nameof(DeviceCatalogItem.HardwareRevision)
                    ? string.Empty
                    : source.HardwareRevision,
            FirmwareVersion =
                propertyName == nameof(DeviceCatalogItem.FirmwareVersion)
                    ? string.Empty
                    : source.FirmwareVersion,
            Location = propertyName == nameof(DeviceCatalogItem.Location)
                ? string.Empty
                : source.Location,
            Capabilities = source.Capabilities.ToList()
        };
    }

    private static DeviceCatalogItem CopyItem(
        DeviceCatalogItem source,
        string? code = null,
        string? name = null,
        List<DeviceCapability>? capabilities = null)
    {
        return new DeviceCatalogItem
        {
            Code = code ?? source.Code,
            Name = name ?? source.Name,
            HardwareModel = source.HardwareModel,
            HardwareRevision = source.HardwareRevision,
            FirmwareVersion = source.FirmwareVersion,
            Location = source.Location,
            Capabilities = capabilities ?? source.Capabilities.ToList()
        };
    }
}
