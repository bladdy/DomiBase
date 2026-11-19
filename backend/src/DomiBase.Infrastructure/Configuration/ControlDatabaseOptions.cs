namespace DomiBase.Infrastructure.Configuration;

/// <summary>
/// Connection settings for the Control Plane metadata database (<c>domibase_control</c>).
/// This database is never reachable from public Runtime keys.
/// </summary>
public sealed class ControlDatabaseOptions
{
    public required string ConnectionString { get; init; }
}
