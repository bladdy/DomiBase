namespace DomiBase.Provisioning;

/// <summary>
/// Privileged connection settings used only by the provisioning pipeline.
/// This connection is never exposed to the Runtime or to clients.
/// </summary>
public sealed class ProvisioningOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>Maintenance database used to run <c>CREATE DATABASE</c> (cannot run inside a transaction).</summary>
    public string AdminDatabase { get; init; } = "postgres";
}
