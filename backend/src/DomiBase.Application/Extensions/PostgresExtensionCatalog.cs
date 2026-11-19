namespace DomiBase.Application.Extensions;

/// <summary>
/// Static allowlist of PostgreSQL extensions the owner may manage. Every entry was checked
/// as <c>trusted</c> and available in the pinned PostgreSQL 18 alpine image. Nothing outside
/// this list can reach a privileged connection, so no SQL text is ever accepted from clients.
/// </summary>
public static class PostgresExtensionCatalog
{
    public sealed record Definition(string Name, string Description);

    public static readonly IReadOnlyList<Definition> All =
    [
        new("btree_gin", "GIN operator classes for scalar types such as int and text."),
        new("btree_gist", "GiST operator classes for scalar types and exclusion constraints."),
        new("citext", "Case-insensitive character string type."),
        new("hstore", "Key/value store inside a single PostgreSQL value."),
        new("intarray", "Additional operators and indexes for integer arrays."),
        new("ltree", "Labels and paths for hierarchical data."),
        new("pg_trgm", "Trigram similarity matching and indexes."),
        new("pgcrypto", "Cryptographic functions such as digest, hmac and gen_random_uuid."),
        new("unaccent", "Text search dictionary that removes accents."),
        new("uuid-ossp", "UUID generation functions from the OSSP library.")
    ];

    private static readonly HashSet<string> AllowedNames =
        new(All.Select(definition => definition.Name), StringComparer.Ordinal);

    private static readonly Dictionary<string, Definition> ByName =
        All.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    /// <summary>Names in deterministic order for catalog queries and unit assertions.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(definition => definition.Name).ToArray();

    public static bool IsAllowed(string? name) => name is not null && AllowedNames.Contains(name);

    public static bool TryGet(string? name, out Definition definition)
    {
        if (name is not null && ByName.TryGetValue(name, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    public static string Describe(string name) =>
        TryGet(name, out var definition) ? definition.Description : string.Empty;
}
