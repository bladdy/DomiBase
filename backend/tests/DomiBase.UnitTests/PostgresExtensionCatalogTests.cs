using DomiBase.Application.Extensions;

namespace DomiBase.UnitTests;

/// <summary>
/// The extension allowlist is a security boundary: it must stay exact, finite and deterministic.
/// </summary>
public class PostgresExtensionCatalogTests
{
    [Fact]
    public void All_ContainsOnlyVerifiedTrustedExtensions()
    {
        string[] expected =
        [
            "btree_gin", "btree_gist", "citext", "hstore", "intarray",
            "ltree", "pg_trgm", "pgcrypto", "unaccent", "uuid-ossp"
        ];

        Assert.Equal(expected, PostgresExtensionCatalog.Names);
        Assert.All(PostgresExtensionCatalog.All, definition => Assert.False(string.IsNullOrWhiteSpace(definition.Description)));
    }

    [Fact]
    public void Names_AreUniqueAndSorted()
    {
        var names = PostgresExtensionCatalog.Names;

        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    [Theory]
    [InlineData("pgcrypto")]
    [InlineData("uuid-ossp")]
    [InlineData("pg_trgm")]
    public void IsAllowed_AcceptsExactCatalogNames(string name)
    {
        Assert.True(PostgresExtensionCatalog.IsAllowed(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PGCRYPTO")]
    [InlineData(" pgcrypto")]
    [InlineData("dblink")]
    [InlineData("plpython3u")]
    [InlineData("pgcrypto\"; DROP EXTENSION \"pgcrypto")]
    public void IsAllowed_RejectsUnknownOrInjectedNames(string? name)
    {
        Assert.False(PostgresExtensionCatalog.IsAllowed(name));
    }

    [Fact]
    public void TryGet_ReturnsTheAllowlistedDefinition()
    {
        Assert.True(PostgresExtensionCatalog.TryGet("uuid-ossp", out var definition));
        Assert.Equal("uuid-ossp", definition.Name);
        Assert.Contains("UUID", definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReturnsEmptyForUnknownNames()
    {
        Assert.Equal(string.Empty, PostgresExtensionCatalog.Describe("dblink"));
    }
}
