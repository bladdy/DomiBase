using Npgsql;

namespace DomiBase.Provisioning;

/// <summary>
/// Npgsql-backed provisioning connection factory. Connection strings and credentials
/// are never logged. Provisioning is infrequent, so connections are opened per step.
/// </summary>
public sealed class ProvisioningConnectionFactory(ProvisioningOptions options) : IProvisioningConnectionFactory
{
    public async ValueTask<NpgsqlConnection> OpenAsync(
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            ApplicationName = "domibase-provisioning"
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database;
        }
        else if (!string.IsNullOrWhiteSpace(options.AdminDatabase))
        {
            builder.Database = options.AdminDatabase;
        }

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public string Describe() => $"provisioning:admin-database={options.AdminDatabase}";
}
