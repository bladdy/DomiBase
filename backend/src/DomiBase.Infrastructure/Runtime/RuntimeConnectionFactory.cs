using DomiBase.Application.Runtime;
using Npgsql;

namespace DomiBase.Infrastructure.Runtime;

/// <summary>
/// Opens project database connections using the project's dedicated runtime login.
/// Credentials are never logged and connection pooling is per project/credential set.
/// </summary>
public sealed class RuntimeConnectionFactory(RuntimeOptions options)
{
    public async ValueTask<NpgsqlConnection> OpenAsync(
        RuntimeProjectContext context,
        CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = context.DatabaseName,
            Username = context.RuntimeUsername,
            Password = context.RuntimePassword,
            SslMode = options.SslMode,
            ApplicationName = "domibase-runtime",
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = options.MaxPoolSize,
            Timeout = options.ConnectionTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
            NoResetOnClose = false
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Log-safe description; never includes the password.</summary>
    public static string Describe(RuntimeProjectContext context) =>
        $"runtime:database={context.DatabaseName};role={context.RuntimeUsername}";
}
