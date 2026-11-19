using System.Text.Json;
using DomiBase.Application.Projects;

namespace DomiBase.Application.Query;

/// <summary>A bounded, read-only SQL result. Rows are already JSON-safe PostgreSQL values.</summary>
public sealed record SqlQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<JsonElement> Rows,
    int RowCount,
    bool Truncated,
    long DurationMs);

/// <summary>A failure from the privileged query runner with a safe, transport-neutral code.</summary>
public sealed class QueryExecutionException(string message, ControlErrorCode code, string? hint = null)
    : Exception(message)
{
    public ControlErrorCode Code { get; } = code;

    public string? Hint { get; } = hint;
}

/// <summary>
/// Executes an already validated read-only statement inside a single project database.
/// Implemented in the provisioning module; never registered in the Runtime Plane.
/// </summary>
public interface IProjectQueryRunner
{
    Task<SqlQueryResult> RunAsync(
        Projects.ProjectResourceNames names,
        string statement,
        int maxRows,
        int timeoutMs,
        CancellationToken cancellationToken = default);
}
