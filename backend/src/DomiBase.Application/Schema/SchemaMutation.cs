namespace DomiBase.Application.Schema;

/// <summary>
/// Functional PostgreSQL roles a client may target. PostgreSQL role names are never
/// accepted from the caller: they are derived from trusted project metadata instead.
/// </summary>
public enum SchemaFunctionalRole
{
    Anon,
    Authenticated
}

/// <summary>RLS commands a policy may grant. Mapped to physical privileges by the admin layer.</summary>
public enum PolicyCommand
{
    Select,
    Insert,
    Update,
    Delete
}

/// <summary>An optional foreign key. Both identifiers are validated like every other identifier.</summary>
public sealed record ColumnReference(string Table, string Column);

/// <summary>
/// A validated column definition. <see cref="DefaultSql"/> is produced by
/// <see cref="SchemaDdlValidator.TryBuildDefault"/> and is never raw caller SQL.
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    string Type,
    bool Nullable,
    bool PrimaryKey,
    bool Unique,
    string? DefaultSql,
    ColumnReference? Reference,
    bool Identity = false);

/// <summary>A policy exposed by a project table, translated to logical DomiBase roles.</summary>
public sealed record SchemaPolicy(
    string Name,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Roles,
    string? Using,
    string? WithCheck);

/// <summary>
/// Base type for every administrative schema change. The concrete records carry only
/// validated values; no operation accepts a SQL string from the caller.
/// </summary>
public abstract record SchemaMutation(string AuditAction);

public sealed record CreateTableMutation(string Table, IReadOnlyList<ColumnDefinition> Columns)
    : SchemaMutation("schema.table.create");

public sealed record DropTableMutation(string Table)
    : SchemaMutation("schema.table.drop");

public sealed record AddColumnMutation(string Table, ColumnDefinition Column)
    : SchemaMutation("schema.column.add");

public sealed record AlterColumnMutation(
    string Table,
    string Column,
    string? NewName,
    string? Type,
    bool? Nullable)
    : SchemaMutation("schema.column.alter");

public sealed record DropColumnMutation(string Table, string Column)
    : SchemaMutation("schema.column.drop");

public sealed record CreateIndexMutation(
    string Table,
    string Name,
    IReadOnlyList<string> Columns,
    bool Unique)
    : SchemaMutation("schema.index.create");

public sealed record DropIndexMutation(string Table, string Name)
    : SchemaMutation("schema.index.drop");

public sealed record CreatePolicyMutation(
    string Table,
    string Name,
    IReadOnlyList<PolicyCommand> Commands,
    IReadOnlyList<SchemaFunctionalRole> Roles,
    string Using,
    string WithCheck)
    : SchemaMutation("schema.policy.create");

/// <summary>Full replacement of a policy definition, applied atomically in one transaction.</summary>
public sealed record AlterPolicyMutation(
    string Table,
    string Name,
    IReadOnlyList<PolicyCommand> Commands,
    IReadOnlyList<SchemaFunctionalRole> Roles,
    string Using,
    string WithCheck)
    : SchemaMutation("schema.policy.alter");

public sealed record DropPolicyMutation(string Table, string Name)
    : SchemaMutation("schema.policy.drop");

/// <summary>Non-secret confirmation returned after a successful DDL operation.</summary>
public sealed record SchemaMutationSummary(string Action, string Table, string? Object);

/// <summary>
/// Friendly failure from the privileged SQL layer. It never carries PostgreSQL SQL text,
/// connection details or role names derived from other projects.
/// </summary>
public sealed class SchemaAdminException(string message, string? hint = null, bool validation = false)
    : Exception(message)
{
    public string? Hint { get; } = hint;

    public bool IsValidation { get; } = validation;
}
