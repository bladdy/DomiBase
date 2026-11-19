using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

public class SlugValidatorTests
{
    [Theory]
    [InlineData("paneles-solares")]
    [InlineData("a")]
    [InlineData("project1")]
    [InlineData("my-cool-project-2")]
    public void IsValid_AcceptsValidSlugs(string slug) =>
        Assert.True(SlugValidator.IsValid(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("Upper")]
    [InlineData("under_score")]
    [InlineData("with space")]
    [InlineData("dot.dot")]
    [InlineData("1starts-with-digit")]
    [InlineData("acentuación")]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("admin")]
    public void IsValid_RejectsInvalidSlugs(string? slug) =>
        Assert.False(SlugValidator.IsValid(slug));

    [Fact]
    public void IsValid_RejectsSlugsLongerThanLimit()
    {
        var slug = new string('a', ProjectNaming.MaxSlugLength + 1);

        Assert.False(SlugValidator.IsValid(slug));
    }

    [Theory]
    [InlineData("  Paneles Solares  ", "paneles-solares")]
    [InlineData("paneles_solares", "paneles-solares")]
    [InlineData("paneles--solares", "paneles-solares")]
    [InlineData("Paneles-Solares", "paneles-solares")]
    [InlineData("-paneles-", "paneles")]
    public void TryNormalize_NormalizesInput(string input, string expected)
    {
        Assert.True(SlugValidator.TryNormalize(input, out var slug, out var error));
        Assert.Null(error);
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*invalid*")]
    [InlineData("postgres")]
    public void TryNormalize_RejectsInvalidInput(string input)
    {
        Assert.False(SlugValidator.TryNormalize(input, out var slug, out var error));
        Assert.Equal(string.Empty, slug);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ToDatabaseName_ReplacesHyphensWithUnderscores()
    {
        Assert.Equal("domibase_paneles_solares", ProjectNaming.ToDatabaseName("paneles-solares"));
    }

    [Fact]
    public void DerivedIdentifiers_StayWithinPostgreSqlLimit()
    {
        var slug = new string('a', ProjectNaming.MaxSlugLength);

        Assert.True(ProjectNaming.ToDatabaseName(slug).Length <= ProjectNaming.MaxDatabaseNameLength);
        Assert.True(ProjectNaming.ToOwnerRole(slug).Length <= ProjectNaming.MaxDatabaseNameLength);
        Assert.True(ProjectNaming.ToRuntimeRole(slug).Length <= ProjectNaming.MaxDatabaseNameLength);
        Assert.True(ProjectNaming.ToAnonymousRole(slug).Length <= ProjectNaming.MaxDatabaseNameLength);
    }
}
