namespace DomiBase.Domain.Projects;

/// <summary>
/// Audit trail of a single provisioning step for a project. Used to make
/// provisioning idempotent, retryable and compensable.
/// </summary>
public sealed class ProvisioningOperation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Stable step identifier (for example <c>create_database</c>).</summary>
    public required string Step { get; set; }

    public ProvisioningStatus Status { get; set; } = ProvisioningStatus.Pending;

    public int Attempt { get; set; }

    public string? Error { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
