namespace DomiBase.Application.Schema;

/// <summary>A user table exposed by a project database. Internal schemas are never included.</summary>
public sealed record SchemaTable(string Name);

/// <summary>A validated description of one table column, safe to display in the panel.</summary>
public sealed record SchemaColumn(
    string Name,
    string Type,
    string DataType,
    bool Nullable,
    string? Default,
    bool PrimaryKey,
    bool Unique,
    bool Identity,
    string? ForeignTable,
    string? ForeignColumn);

/// <summary>A view (ordinary or materialized) exposed by a project database.</summary>
public sealed record SchemaView(string Name);

/// <summary>A user-defined SQL function in a project database.</summary>
public sealed record SchemaFunction(string Name, string Arguments, string ReturnType);
