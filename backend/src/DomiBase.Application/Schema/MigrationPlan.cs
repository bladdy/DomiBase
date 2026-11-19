namespace DomiBase.Application.Schema;

/// <summary>
/// A versioned, immutable unit of administrative schema changes. The client sends typed
/// operations; the server computes <see cref="Checksum"/> so the same version can never be
/// silently redefined with different contents.
/// </summary>
public sealed record MigrationPlan(
    int Version,
    string Name,
    IReadOnlyList<SchemaMutation> Operations,
    string Checksum);

/// <summary>A migration already recorded in the private history of a project database.</summary>
public sealed record AppliedMigration(
    int Version,
    string Name,
    string Checksum,
    int OperationCount,
    DateTimeOffset AppliedAt);

/// <summary>Result of applying a migration, including whether it was an idempotent no-op.</summary>
public sealed record MigrationApplyOutcome(AppliedMigration Migration, bool AlreadyApplied);

/// <summary>Non-secret confirmation returned after applying a migration.</summary>
public sealed record MigrationSummary(
    int Version,
    string Name,
    string Checksum,
    int OperationCount,
    bool AlreadyApplied);
