using DomiBase.Application.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Privileged DDL access to a single project database. Implemented by the provisioning
/// module with its own connection string; never registered in the Runtime Plane. Every
/// argument is pre-validated by <see cref="SchemaDdlValidator"/> and derived from trusted
/// project metadata, so no SQL text or PostgreSQL role name crosses this boundary.
/// </summary>
public interface IProjectSchemaAdmin
{
    Task ExecuteAsync(
        ProjectResourceNames names,
        SchemaMutation mutation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaPolicy>> ListPoliciesAsync(
        ProjectResourceNames names,
        string table,
        CancellationToken cancellationToken = default);

    /// <summary>Non-secret description usable in diagnostics.</summary>
    string Describe();
}
