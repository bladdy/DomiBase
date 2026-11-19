using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using DomiBase.Domain.Auditing;

namespace DomiBase.Application.Query;

/// <summary>
/// Control Plane orchestration for the administrative SQL editor. Validates and bounds the
/// statement before any connection opens, runs it only against a Ready project through the
/// privileged runner, and records a non-secret audit entry (a one-way fingerprint, never the SQL).
/// </summary>
public sealed class SqlQueryService(
    IProjectRepository repository,
    IProjectQueryRunner runner,
    ProjectSchemaResolver resolver)
{
    public const int DefaultMaxRows = 100;
    public const int MaxRowsLimit = 1000;
    public const int DefaultTimeoutMs = 10_000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30_000;

    public async Task<ControlResult<SqlQueryResult>> ExecuteAsync(
        Guid projectId,
        string? sql,
        int? maxRows,
        int? timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (!ReadOnlySqlValidator.TryValidate(sql, out var statement, out var validationError))
        {
            return ControlResult<SqlQueryResult>.Fail(
                ControlErrorCode.Validation,
                "The SQL statement is not allowed.",
                validationError);
        }

        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<SqlQueryResult>.Fail(
                resolved.Error.Code,
                resolved.Error.Message,
                resolved.Error.Hint);
        }

        var boundedRows = maxRows is null
            ? DefaultMaxRows
            : Math.Clamp(maxRows.Value, 1, MaxRowsLimit);
        var boundedTimeout = timeoutMs is null
            ? DefaultTimeoutMs
            : Math.Clamp(timeoutMs.Value, MinTimeoutMs, MaxTimeoutMs);

        SqlQueryResult result;
        try
        {
            result = await runner.RunAsync(
                resolved.Names!,
                statement,
                boundedRows,
                boundedTimeout,
                cancellationToken);
        }
        catch (QueryExecutionException exception)
        {
            return ControlResult<SqlQueryResult>.Fail(
                exception.Code,
                exception.Message,
                exception.Hint);
        }

        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = "sql.query",
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(new
                {
                    database = resolved.Names!.DatabaseName,
                    rowCount = result.RowCount,
                    truncated = result.Truncated,
                    durationMs = result.DurationMs,
                    maxRows = boundedRows,
                    timeoutMs = boundedTimeout,
                    sqlFingerprint = Fingerprint(statement)
                })
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return ControlResult<SqlQueryResult>.Ok(result);
    }

    private static string Fingerprint(string statement)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(statement));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
