using Npgsql;

namespace DomiBase.Provisioning;

/// <summary>
/// Opens privileged PostgreSQL connections for provisioning steps.
/// Only the Control Plane provisioning pipeline may consume this abstraction.
/// Connections are short-lived and always closed by the caller.
/// </summary>
public interface IProvisioningConnectionFactory
{
    /// <summary>
    /// Opens a privileged connection. When <paramref name="database"/> is provided,
    /// the connection targets that database (used for per-database permissions).
    /// </summary>
    ValueTask<NpgsqlConnection> OpenAsync(
        string? database = null,
        CancellationToken cancellationToken = default);

    /// <summary>Non-secret description usable in logs and diagnostics.</summary>
    string Describe();
}
