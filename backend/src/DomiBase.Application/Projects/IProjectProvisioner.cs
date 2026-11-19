using DomiBase.Domain.Projects;

namespace DomiBase.Application.Projects;

public sealed record ProvisioningStepResult(string Step, bool Succeeded, string? Error);

public sealed record ProvisioningOutcome(
    bool Succeeded,
    string? Error,
    IReadOnlyList<ProvisioningStepResult> Steps);

public sealed record DeprovisionOutcome(
    bool Succeeded,
    string? Error,
    IReadOnlyList<ProvisioningStepResult> Steps);

/// <summary>
/// Persists the lifecycle of a privileged PostgreSQL step while it runs. The sink is
/// supplied by the Control Plane; the provisioning module only reports state changes.
/// </summary>
public interface IProvisioningStepSink
{
    Task<Guid> BeginAsync(string step, CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid operationId,
        ProvisioningStatus status,
        string? error,
        CancellationToken cancellationToken = default);
}

public static class ProvisioningSteps
{
    public const string Roles = "roles";
    public const string Database = "database";
    public const string DatabasePermissions = "database_permissions";
    public const string AuthSchema = "auth_schema";
    public const string DatabaseCleanup = "database_cleanup";
    public const string RoleCleanup = "role_cleanup";
}

/// <summary>
/// Provisions the independent PostgreSQL database and restricted roles for a project.
/// Implementations must be idempotent, safe to retry and able to compensate partial work.
/// </summary>
public interface IProjectProvisioner
{
    Task<ProvisioningOutcome> ProvisionAsync(
        Project project,
        ProjectRuntimeCredentials runtimeCredentials,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null);

    /// <summary>
    /// Idempotent Auth upgrade for projects provisioned before Auth existed: ensures the
    /// <c>authenticated</c> role, its grants and the <c>auth</c> schema. Safe to call on
    /// projects that are already <c>Ready</c>.
    /// </summary>
    Task EnsureAuthSchemaAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently removes every PostgreSQL resource that belongs to the project:
    /// the database followed by its four dedicated roles. Resources owned by a different
    /// role are never removed.
    /// </summary>
    Task<DeprovisionOutcome> DeprovisionAsync(
        Project project,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null);

    /// <summary>
    /// Compensates partially created resources after a failed provisioning run. It inspects
    /// the real PostgreSQL state instead of trusting the reported step results.
    /// </summary>
    Task<DeprovisionOutcome> CompensateAsync(
        Project project,
        CancellationToken cancellationToken = default,
        IProvisioningStepSink? sink = null);
}
