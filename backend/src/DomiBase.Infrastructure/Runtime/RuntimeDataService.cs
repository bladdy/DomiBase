using System.Data;
using System.Globalization;
using System.Text.Json;
using DomiBase.Application.Auth;
using DomiBase.Application.Realtime;
using DomiBase.Application.Runtime;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DomiBase.Infrastructure.Runtime;

/// <summary>
/// Executes Runtime operations against a project database. Every request runs in a short
/// transaction that first switches to the project's anonymous functional role and installs
/// request claims with transaction-local settings; nothing survives the transaction.
/// </summary>
public sealed class RuntimeDataService(
    RuntimeConnectionFactory connections,
    RuntimeOptions options,
    IRealtimeNotifier notifier,
    ILogger<RuntimeDataService> logger) : IRuntimeDataService
{
    private const int MaxTableNameLength = 63;

    public Task<RuntimeResultSet> SelectAsync(
        RuntimeProjectContext context,
        string table,
        RuntimeReadOptions readOptions,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(
            context,
            table,
            user,
            null,
            (connection, transaction, metadata) =>
            {
                var sql = RuntimeQueryBuilder.BuildSelect(metadata, readOptions);
                return ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
            },
            cancellationToken);

    public Task<RuntimeResultSet> InsertAsync(
        RuntimeProjectContext context,
        string table,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyList<string>? returning,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(
            context,
            table,
            user,
            "INSERT",
            async (connection, transaction, metadata) =>
            {
                var allRows = new List<IReadOnlyDictionary<string, object?>>();
                var affected = 0;

                foreach (var values in rows)
                {
                    var sql = RuntimeQueryBuilder.BuildInsert(metadata, values, returning);
                    var result = await ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
                    allRows.AddRange(result.Rows);
                    affected += result.Affected;
                }

                return new RuntimeResultSet(allRows, affected);
            },
            cancellationToken);

    public Task<RuntimeResultSet> UpdateAsync(
        RuntimeProjectContext context,
        string table,
        IReadOnlyDictionary<string, string?> values,
        RuntimeWriteOptions writeOptions,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(
            context,
            table,
            user,
            "UPDATE",
            (connection, transaction, metadata) =>
            {
                var sql = RuntimeQueryBuilder.BuildUpdate(metadata, values, writeOptions);
                return ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
            },
            cancellationToken);

    public Task<RuntimeResultSet> DeleteAsync(
        RuntimeProjectContext context,
        string table,
        RuntimeWriteOptions writeOptions,
        AccessTokenClaims? user = null,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(
            context,
            table,
            user,
            "DELETE",
            (connection, transaction, metadata) =>
            {
                var sql = RuntimeQueryBuilder.BuildDelete(metadata, writeOptions);
                return ExecuteSqlAsync(connection, transaction, sql, cancellationToken);
            },
            cancellationToken);

    private async Task<RuntimeResultSet> ExecuteInTransactionAsync(
        RuntimeProjectContext context,
        string table,
        AccessTokenClaims? user,
        string? operation,
        Func<NpgsqlConnection, NpgsqlTransaction, RuntimeTableMetadata, Task<RuntimeResultSet>> operationDelegate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table) || table.Length > MaxTableNameLength)
        {
            throw TableNotFound(table);
        }

        await using var connection = await connections.OpenAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            await ApplyRequestContextAsync(connection, transaction, context, user, cancellationToken);

            var metadata = await LoadTableMetadataAsync(connection, transaction, table, cancellationToken)
                ?? throw TableNotFound(table);

            var result = await operationDelegate(connection, transaction, metadata);

            await transaction.CommitAsync(cancellationToken);

            if (operation is not null)
            {
                _ = NotifyAsync(context, table, operation, result);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackQuietlyAsync(transaction);
            throw;
        }
        catch (Exception exception)
        {
            await RollbackQuietlyAsync(transaction);
            throw Translate(exception, context, table);
        }
    }

    private static async Task ApplyRequestContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeProjectContext context,
        AccessTokenClaims? user,
        CancellationToken cancellationToken)
    {
        var isAuthenticated = user is not null &&
            user.ProjectId == context.ProjectId &&
            user.ApiKeyId == context.ApiKeyId &&
            string.Equals(user.Role, AccessTokenClaims.AuthenticatedRole, StringComparison.Ordinal);

        var role = isAuthenticated ? context.AuthenticatedRole : context.AnonymousRole;

        // Transaction-local role switch: the runtime login NOINHERITs privileges and only
        // becomes the functional role inside this transaction.
        await using (var roleCommand = new NpgsqlCommand(
                         "SELECT set_config('role', @role, true), set_config('search_path', 'public', true)",
                         connection,
                         transaction))
        {
            roleCommand.Parameters.AddWithValue("role", role);
            await roleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var claims = isAuthenticated
            ? JsonSerializer.Serialize(new
            {
                role = "authenticated",
                sub = user!.UserId.ToString(),
                project_id = context.ProjectId.ToString(),
                api_key_id = context.ApiKeyId.ToString()
            })
            : JsonSerializer.Serialize(new
            {
                role = "anon",
                project_id = context.ProjectId.ToString(),
                api_key_id = context.ApiKeyId.ToString()
            });

        await using var claimsCommand = new NpgsqlCommand(
            "SELECT set_config('request.jwt.claims', @claims, true), " +
            "set_config('domibase.project_id', @project, true), " +
            "set_config('domibase.api_key_id', @key, true)",
            connection,
            transaction);

        claimsCommand.Parameters.AddWithValue("claims", claims);
        claimsCommand.Parameters.AddWithValue("project", context.ProjectId.ToString());
        claimsCommand.Parameters.AddWithValue("key", context.ApiKeyId.ToString());

        await claimsCommand.ExecuteNonQueryAsync(cancellationToken);

        if (isAuthenticated)
        {
            await using var userCommand = new NpgsqlCommand(
                "SELECT set_config('domibase.user_id', @userId, true)",
                connection,
                transaction);
            userCommand.Parameters.AddWithValue("userId", user!.UserId.ToString());
            await userCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<RuntimeTableMetadata?> LoadTableMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        bool rowLevelSecurity;
        bool tableSelect;
        bool tableInsert;
        bool tableUpdate;
        bool tableDelete;

        const string tableSql = """
            SELECT c.relrowsecurity,
                   has_table_privilege(c.oid, 'SELECT'),
                   has_table_privilege(c.oid, 'INSERT'),
                   has_table_privilege(c.oid, 'UPDATE'),
                   has_table_privilege(c.oid, 'DELETE')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relname = @table
              AND c.relkind IN ('r', 'p')
            """;

        await using (var tableCommand = new NpgsqlCommand(tableSql, connection, transaction))
        {
            tableCommand.Parameters.AddWithValue("table", table);

            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            rowLevelSecurity = reader.GetBoolean(0);
            tableSelect = reader.GetBoolean(1);
            tableInsert = reader.GetBoolean(2);
            tableUpdate = reader.GetBoolean(3);
            tableDelete = reader.GetBoolean(4);
        }

        if (!rowLevelSecurity)
        {
            logger.LogDebug(
                "Table {Table} is not exposed because row-level security is disabled.",
                table);
            return null;
        }

        var columns = new List<RuntimeColumn>();

        const string columnSql = """
            SELECT a.attname,
                   t.typname,
                   has_column_privilege(c.oid, a.attnum, 'SELECT'),
                   has_column_privilege(c.oid, a.attnum, 'INSERT'),
                   has_column_privilege(c.oid, a.attnum, 'UPDATE')
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE n.nspname = 'public'
              AND c.relname = @table
              AND c.relkind IN ('r', 'p')
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        await using (var columnCommand = new NpgsqlCommand(columnSql, connection, transaction))
        {
            columnCommand.Parameters.AddWithValue("table", table);

            await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new RuntimeColumn(
                    Name: reader.GetString(0),
                    UdtName: reader.GetString(1),
                    CanSelect: reader.GetBoolean(2),
                    CanInsert: reader.GetBoolean(3),
                    CanUpdate: reader.GetBoolean(4)));
            }
        }

        var canSelect = tableSelect || columns.Any(column => column.CanSelect);
        var canInsert = tableInsert || columns.Any(column => column.CanInsert);
        var canUpdate = tableUpdate || columns.Any(column => column.CanUpdate);

        if (!canSelect && !canInsert && !canUpdate && !tableDelete)
        {
            return null;
        }

        return new RuntimeTableMetadata(
            Name: table,
            CanSelect: canSelect,
            CanInsert: canInsert,
            CanUpdate: canUpdate,
            CanDelete: tableDelete,
            Columns: columns);
    }

    private async Task<RuntimeResultSet> ExecuteSqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeSql sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.CommandText, connection, transaction)
        {
            CommandTimeout = options.CommandTimeoutSeconds
        };

        foreach (var parameter in sql.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(parameter.Name, NpgsqlDbType.Text)
            {
                Value = parameter.Value ?? (object)DBNull.Value
            });
        }

        if (sql.ResultColumns.Count == 0)
        {
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            return new RuntimeResultSet([], Math.Max(affected, 0));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                rows.Add(row);
            }
        }

        return new RuntimeResultSet(rows, rows.Count);
    }

    private RuntimeQueryException Translate(
        Exception exception,
        RuntimeProjectContext context,
        string table)
    {
        if (exception is RuntimeQueryException runtime)
        {
            return runtime;
        }

        if (exception is PostgresException postgres)
        {
            logger.LogWarning(
                "Runtime PostgreSQL error {SqlState} on project {ProjectId}, table {Table}: {Message}",
                postgres.SqlState,
                context.ProjectId,
                table,
                postgres.MessageText);

            return postgres.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => new RuntimeQueryException(
                    409, "CONFLICT", "A row with the same unique values already exists."),
                PostgresErrorCodes.ForeignKeyViolation => new RuntimeQueryException(
                    409, "FOREIGN_KEY_VIOLATION", "The operation references a row that does not exist or is still referenced."),
                PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.CheckViolation => new RuntimeQueryException(
                    422, "VALIDATION_ERROR", "A value does not satisfy the table constraints."),
                PostgresErrorCodes.InvalidTextRepresentation => new RuntimeQueryException(
                    400, "INVALID_VALUE", "A value has an invalid format for its column."),
                PostgresErrorCodes.InsufficientPrivilege => new RuntimeQueryException(
                    403, "FORBIDDEN", "The project permissions deny this operation."),
                PostgresErrorCodes.UndefinedTable => TableNotFound(table),
                PostgresErrorCodes.UndefinedColumn => new RuntimeQueryException(
                    400, "INVALID_COLUMN", "A column is not available."),
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => new RuntimeQueryException(
                    409, "CONFLICT", "The operation conflicted with another transaction. Retry it."),
                PostgresErrorCodes.QueryCanceled => new RuntimeQueryException(
                    504, "QUERY_TIMEOUT", "The query took too long and was cancelled."),
                PostgresErrorCodes.TooManyConnections => new RuntimeQueryException(
                    503, "DATABASE_UNAVAILABLE", "The project database is temporarily unavailable."),
                _ when postgres.SqlState.StartsWith("22", StringComparison.Ordinal) => new RuntimeQueryException(
                    400, "INVALID_VALUE", "A value is invalid for its column."),
                _ when postgres.SqlState.StartsWith("08", StringComparison.Ordinal) => new RuntimeQueryException(
                    503, "DATABASE_UNAVAILABLE", "The project database is temporarily unavailable."),
                _ => new RuntimeQueryException(
                    500, "DATABASE_ERROR", "The project database rejected the operation.")
            };
        }

        if (exception is NpgsqlException or TimeoutException)
        {
            logger.LogError(
                exception,
                "Runtime database connection failure for project {ProjectId} ({Database}).",
                context.ProjectId,
                RuntimeConnectionFactory.Describe(context));

            return new RuntimeQueryException(
                503, "DATABASE_UNAVAILABLE", "The project database is temporarily unavailable.");
        }

        logger.LogError(
            exception,
            "Unhandled runtime error for project {ProjectId}, table {Table}.",
            context.ProjectId,
            table);

        return new RuntimeQueryException(500, "INTERNAL_ERROR", "An unexpected error occurred.");
    }

    private async Task NotifyAsync(
        RuntimeProjectContext context,
        string table,
        string operation,
        RuntimeResultSet result)
    {
        try
        {
            var rowId = ExtractRowId(result);
            var payload = new RealtimeEvent(table, operation, rowId, DateTimeOffset.UtcNow);
            await notifier.NotifyAsync(context.ProjectId, payload);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to enqueue realtime notification for project {ProjectId}, table {Table}, operation {Operation}.",
                context.ProjectId,
                table,
                operation);
        }
    }

    private static string? ExtractRowId(RuntimeResultSet result)
    {
        foreach (var row in result.Rows)
        {
            if (row.TryGetValue("id", out var value) && value is not null)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static async Task RollbackQuietlyAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // Connection already broken; disposing it is enough.
        }
    }

    private static RuntimeQueryException TableNotFound(string table) =>
        new(404, "TABLE_NOT_FOUND", $"Table '{table}' is not exposed.");
}
