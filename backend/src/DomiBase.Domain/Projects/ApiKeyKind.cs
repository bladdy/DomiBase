namespace DomiBase.Domain.Projects;

/// <summary>
/// Kind of API key issued for a project.
/// </summary>
public enum ApiKeyKind
{
    /// <summary>Public key (<c>db_public_</c>): identifies the project, never grants data access by itself.</summary>
    Public = 0,

    /// <summary>
    /// Secret key (<c>db_secret_</c>): server-side only. It is never accepted by the public
    /// <c>apikey</c> header of the Runtime data API and grants no elevated PostgreSQL role.
    /// </summary>
    Secret = 1
}
