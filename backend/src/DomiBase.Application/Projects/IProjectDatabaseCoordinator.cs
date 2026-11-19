using DomiBase.Application.Backups;

namespace DomiBase.Application.Projects;

/// <summary>
/// Session-scoped advisory lock. Held for the whole dump/restore process and released on
/// disposal; uses the same key as provisioning so the two operations cannot overlap.
/// </summary>
public interface IProjectDatabaseLock : IAsyncDisposable
{
    bool Acquired { get; }
}

/// <summary>
/// Privileged coordination for per-project database operations that cannot be expressed
/// as a single Npgsql command (logical dumps and restores).
/// </summary>
public interface IProjectDatabaseCoordinator
{
    PgConnectionSettings ConnectionSettings { get; }

    Task<IProjectDatabaseLock> TryAcquireAsync(string databaseName, CancellationToken cancellationToken = default);

    /// <summary>Drops stale runtime connections so <c>pg_restore --clean</c> is not blocked.</summary>
    Task TerminateOtherConnectionsAsync(string databaseName, CancellationToken cancellationToken = default);
}
