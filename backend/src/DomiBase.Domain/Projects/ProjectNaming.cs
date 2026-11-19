namespace DomiBase.Domain.Projects;

/// <summary>
/// Deterministic PostgreSQL naming for projects.
/// PostgreSQL identifiers are limited to 63 bytes, and roles are cluster-wide,
/// so every role name is derived from the project database name.
/// </summary>
public static class ProjectNaming
{
    public const string DatabasePrefix = "domibase_";

    /// <summary>
    /// Maximum slug length. Chosen so that the longest derived role
    /// (<c>domibase_&lt;slug&gt;_runtime</c>) stays under PostgreSQL's 63-byte identifier limit.
    /// </summary>
    public const int MaxSlugLength = 40;

    public const int MaxDatabaseNameLength = 63;

    /// <summary>Maps a validated slug to its project database name (<c>domibase_&lt;slug&gt;</c>, hyphens become underscores).</summary>
    public static string ToDatabaseName(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return DatabasePrefix + slug.Replace('-', '_');
    }

    /// <summary>Role used for migrations/ownership. Never used by the Runtime.</summary>
    public static string ToOwnerRole(string slug) => ToDatabaseName(slug) + "_owner";

    /// <summary>Least-privileged login role used by the Runtime.</summary>
    public static string ToRuntimeRole(string slug) => ToDatabaseName(slug) + "_runtime";

    /// <summary>Functional role for anonymous requests.</summary>
    public static string ToAnonymousRole(string slug) => ToDatabaseName(slug) + "_anon";

    /// <summary>Functional role for authenticated end-user requests (JWT validated).</summary>
    public static string ToAuthenticatedRole(string slug) => ToDatabaseName(slug) + "_authenticated";
}
