using DomiBase.Application.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Private, versioned migration history for a single project database. Implemented in the
/// provisioning module with its privileged connection; never registered in the Runtime Plane.
/// The history lives in an internal schema that the Runtime and the schema inspector cannot see.
/// </summary>
public interface IMigrationStore
{
    Task<MigrationApplyOutcome> ApplyAsync(
        ProjectResourceNames names,
        MigrationPlan plan,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppliedMigration>> ListAsync(
        ProjectResourceNames names,
        CancellationToken cancellationToken = default);

    string Describe();
}
