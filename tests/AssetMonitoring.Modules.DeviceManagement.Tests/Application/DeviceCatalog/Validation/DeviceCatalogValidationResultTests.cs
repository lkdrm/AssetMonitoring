using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.DeviceCatalog.Validation;

public sealed class DeviceCatalogValidationResultTests
{
    [Fact]
    public void ConstructorWithNoErrorsCreatesValidResult()
    {
        var result = new DeviceCatalogValidationResult(
            Array.Empty<DeviceCatalogValidationError>());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ConstructorWithErrorsCreatesInvalidResult()
    {
        var error = new DeviceCatalogValidationError(
            "TestError",
            "Test validation error.");

        var result = new DeviceCatalogValidationResult([error]);

        Assert.False(result.IsValid);
        Assert.Equal(error, Assert.Single(result.Errors));
    }

    [Fact]
    public void ConstructorCopiesTheProvidedErrorCollection()
    {
        var errors = new List<DeviceCatalogValidationError>();
        var result = new DeviceCatalogValidationResult(errors);

        errors.Add(new DeviceCatalogValidationError(
            "LateError",
            "Added after construction."));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ConstructorWithNullErrorsThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceCatalogValidationResult(null!));
    }
}
