using DomiBase.Application.Projects;

namespace DomiBase.Application.Extensions;

/// <summary>
/// Live state of one allowlisted PostgreSQL extension in a single project database.
/// Versions and flags come from the server catalog; the name and description come from
/// the application allowlist, never from caller input.
/// </summary>
public sealed record PostgresExtension(
    string Name,
    string Description,
    bool Trusted,
    string? AvailableVersion,
    string? InstalledVersion,
    bool Available,
    bool Installed);

/// <summary>Result of an idempotent extension installation or removal.</summary>
public sealed record ExtensionMutationResult(string Name, string Action);

/// <summary>A safe failure from the privileged extension catalog or administrator.</summary>
public sealed class ExtensionExecutionException(
    string message,
    ControlErrorCode code = ControlErrorCode.Conflict,
    string? hint = null)
    : Exception(message)
{
    public ControlErrorCode Code { get; } = code;

    public string? Hint { get; } = hint;
}

/// <summary>
/// Read-only access to the extension catalog of one project database. Implemented in the
/// provisioning module; never registered in the Runtime Plane.
/// </summary>
public interface IProjectExtensionCatalog
{
    Task<IReadOnlyList<PostgresExtension>> ListAsync(
        string database,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed extension administration. The application layer validates the extension name
/// against <see cref="PostgresExtensionCatalog"/> before any implementation is called.
/// </summary>
public interface IProjectExtensionAdmin
{
    Task InstallAsync(
        ProjectResourceNames names,
        string name,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(
        ProjectResourceNames names,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Non-secret description usable in diagnostics.</summary>
    string Describe();
}
