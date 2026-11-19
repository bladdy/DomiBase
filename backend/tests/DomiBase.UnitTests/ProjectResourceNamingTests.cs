using DomiBase.Application.Projects;
using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

public class ProjectResourceNamingTests
{
    [Fact]
    public void TryResolve_WithValidProject_ReturnsDeterministicNames()
    {
        var project = Project("paneles-solares");

        var resolved = ProjectResourceNaming.TryResolve(project, out var names, out var error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal("domibase_paneles_solares", names.DatabaseName);
        Assert.Equal("domibase_paneles_solares_owner", names.OwnerRole);
        Assert.Equal("domibase_paneles_solares_runtime", names.RuntimeRole);
        Assert.Equal("domibase_paneles_solares_anon", names.AnonymousRole);
        Assert.Equal("domibase_paneles_solares_authenticated", names.AuthenticatedRole);
    }

    [Fact]
    public void TryResolve_WithMismatchedDatabaseName_RefusesToDeriveResources()
    {
        var project = Project("paneles-solares");
        project.DatabaseName = "domibase_otro_proyecto";

        Assert.False(ProjectResourceNaming.TryResolve(project, out _, out var error));
        Assert.Contains("does not match its slug", error);
    }

    [Theory]
    [InlineData("1invalid")]
    [InlineData("postgres")]
    [InlineData("with spaces")]
    [InlineData("")]
    public void TryResolve_WithInvalidSlug_RefusesToDeriveResources(string slug)
    {
        var project = Project("paneles-solares");
        project.Slug = slug;
        project.DatabaseName = "domibase_" + slug.Replace('-', '_');

        Assert.False(ProjectResourceNaming.TryResolve(project, out _, out _));
    }

    [Fact]
    public void TryResolve_WithMaximumLengthSlug_StaysWithinPostgreSqlIdentifierLimit()
    {
        var project = Project(new string('a', ProjectNaming.MaxSlugLength));

        Assert.True(ProjectResourceNaming.TryResolve(project, out var names, out _));
        Assert.Equal(ProjectNaming.MaxDatabaseNameLength, names.AuthenticatedRole.Length);
    }

    private static Project Project(string slug) => new()
    {
        Slug = slug,
        Name = "Test",
        DatabaseName = ProjectNaming.ToDatabaseName(slug)
    };
}
