using AssetMonitoring.Modules.DeviceManagement.Application.DeviceCatalog.Validation;
using AssetMonitoring.Modules.DeviceManagement.Application.Synchronization;

namespace AssetMonitoring.Modules.DeviceManagement.Tests.Application.Synchronization;

public sealed class DeviceCatalogSynchronizationResultTests
{
    [Fact]
    public void ResultWithValidValidationIsApplied()
    {
        var result = CreateResult();

        Assert.True(result.Applied);
    }

    [Fact]
    public void ResultWithInvalidValidationIsNotApplied()
    {
        var validation = new DeviceCatalogValidationResult(
        [
            new DeviceCatalogValidationError(
                "TestError",
                "Test validation error.")
        ]);
        var result = new DeviceCatalogSynchronizationResult(
            validation,
            0,
            0,
            0,
            0,
            0);

        Assert.False(result.Applied);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, true)]
    [InlineData(0, 1, 0, 0, true)]
    [InlineData(0, 0, 1, 0, true)]
    [InlineData(0, 0, 0, 1, true)]
    [InlineData(0, 0, 0, 0, false)]
    public void HasChangesReflectsMutatingCounters(
        int created,
        int updated,
        int retired,
        int restored,
        bool expected)
    {
        var result = CreateResult(
            created,
            updated,
            retired,
            restored);

        Assert.Equal(expected, result.HasChanges);
    }

    private static DeviceCatalogSynchronizationResult CreateResult(
        int created = 0,
        int updated = 0,
        int retired = 0,
        int restored = 0)
    {
        return new DeviceCatalogSynchronizationResult(
            new DeviceCatalogValidationResult(
                Array.Empty<DeviceCatalogValidationError>()),
            created,
            updated,
            0,
            retired,
            restored);
    }
}
