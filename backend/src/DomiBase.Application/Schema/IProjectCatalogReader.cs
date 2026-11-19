namespace DomiBase.Application.Schema;

/// <summary>
/// Read-only catalog access to a single project database. This abstraction exists only
/// for the Control Plane schema inspector: it never reaches the Runtime Plane and never
/// returns credentials, connection strings or arbitrary SQL to callers. Implementations
/// open a short-lived, read-only transaction and close it after the query.
/// </summary>
public interface IProjectCatalogReader
{
    Task<IReadOnlyList<SchemaTable>> ListTablesAsync(string database, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaView>> ListViewsAsync(string database, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaFunction>> ListFunctionsAsync(string database, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaColumn>> ListColumnsAsync(
        string database,
        string table,
        CancellationToken cancellationToken = default);

    /// <summary>Non-secret description usable in diagnostics.</summary>
    string Describe();
}
