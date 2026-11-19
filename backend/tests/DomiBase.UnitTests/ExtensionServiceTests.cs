using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;

namespace DomiBase.UnitTests;

/// <summary>
/// Extension names are validated before the service resolves a project or opens a connection.
/// The null dependencies below make any accidental privileged access fail immediately.
/// </summary>
public class ExtensionServiceTests
{
    [Theory]
    [InlineData("dblink")]
    [InlineData("plpython3u")]
    [InlineData("pgcrypto; DROP EXTENSION pgcrypto")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Mutate_RejectsNamesOutsideTheAllowlistBeforeAnyDependencyIsUsed(string? name)
    {
        var service = new ExtensionService(null!, null!, null!, null!);

        var install = await service.InstallAsync(Guid.NewGuid(), name);
        var uninstall = await service.UninstallAsync(Guid.NewGuid(), name);

        Assert.False(install.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, install.Error!.Code);
        Assert.Equal("The PostgreSQL extension is not allowed.", install.Error.Message);
        Assert.False(uninstall.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, uninstall.Error!.Code);
    }
}
