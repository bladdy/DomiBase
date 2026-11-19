using System.Data;
using System.Diagnostics;
using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Query;
using Npgsql;

namespace DomiBase.Provisioning.Query;

/// <summary>
/// Runs an already validated read-only statement in the target project database. Opens a short
/// privileged connection, switches to the project owner, forces <c>SET TRANSACTION READ ONLY</c>
/// and a statement timeout, caps the result set and serializes every value with
/// <c>to_jsonb</c> so PostgreSQL types survive as JSON without internal SQL leaking to clients.
/// </summary>
public sealed class ProjectQueryRunner(IProvisioningConnectionFactory connectionFactory)
    : IProjectQueryRunner
{
    private const int CommandTimeoutBufferSeconds = 5;

    public async Task<SqlQueryResult> RunAsync(
        ProjectResourceNames names,
        string statement,
        int maxRows,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ExecuteSettingAsync(
                connection,
                transaction,
                $"SET LOCAL ROLE {Q(names.OwnerRole)}",
                timeoutMs,
                cancellationToken);
            await ExecuteSettingAsync(
                connection,
                transaction,
                "SET TRANSACTION READ ONLY",
                timeoutMs,
                cancellationToken);

            await using (var timeout = new NpgsqlCommand(
                "SELECT set_config('statement_timeout', @milliseconds, true)",
                connection,
                transaction))
            {
                timeout.Parameters.AddWithValue("milliseconds", timeoutMs.ToString());
                await timeout.ExecuteScalarAsync(cancellationToken);
            }

            // One extra row reveals truncation without materializing an unbounded result.
            // to_jsonb keeps the last column JSON-safe; q.* preserves the original column names.
            var query = $"SELECT q.*, to_jsonb(q) AS \"__domibase_row\" FROM ({statement}) AS q LIMIT @limit";
            await using var command = new NpgsqlCommand(query, connection, transaction)
            {
                CommandTimeout = (timeoutMs / 1000) + CommandTimeoutBufferSeconds
            };
            command.Parameters.AddWithValue("limit", maxRows + 1);

            var columns = new List<string>();
            var rows = new List<JsonElement>();
            var truncated = false;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                for (var index = 0; index < reader.FieldCount - 1; index++)
                {
                    columns.Add(reader.GetName(index));
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count == maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    using var document = JsonDocument.Parse(reader.GetString(reader.FieldCount - 1));
                    rows.Add(document.RootElement.Clone());
                }
            }

            await transaction.CommitAsync(cancellationToken);
            stopwatch.Stop();

            return new SqlQueryResult(columns, rows, rows.Count, truncated, stopwatch.ElapsedMilliseconds);
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw Translate(exception);
        }
        catch (NpgsqlException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new QueryExecutionException(
                "The database could not complete the query in the configured time.",
                ControlErrorCode.Conflict,
                "Reduce the statement complexity or raise the timeout.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteSettingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = (timeoutMs / 1000) + CommandTimeoutBufferSeconds
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static QueryExecutionException Translate(PostgresException exception) => exception.SqlState switch
    {
        "42601" => new QueryExecutionException(
            "The SQL statement is not valid.",
            ControlErrorCode.Validation),
        "42P01" => new QueryExecutionException(
            "A table referenced by the query does not exist.",
            ControlErrorCode.Validation),
        "42703" => new QueryExecutionException(
            "A column referenced by the query does not exist.",
            ControlErrorCode.Validation),
        "42809" or "25006" => new QueryExecutionException(
            "Only read-only statements are allowed.",
            ControlErrorCode.Validation),
        "57014" => new QueryExecutionException(
            "The query timed out.",
            ControlErrorCode.Conflict,
            "Reduce the statement complexity or raise the timeout."),
        "42501" => new QueryExecutionException(
            "The query is not permitted for this project.",
            ControlErrorCode.Conflict),
        _ => new QueryExecutionException(
            "The database rejected the query.",
            ControlErrorCode.Conflict,
            $"PostgreSQL state {exception.SqlState}.")
    };

    private static string Q(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
