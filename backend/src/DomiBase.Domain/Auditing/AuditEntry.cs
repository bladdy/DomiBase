namespace DomiBase.Domain.Auditing;

/// <summary>
/// Administrative audit record. Never store secrets (tokens, passwords, API keys) here.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Administrative actor identifier (for example <c>admin-token</c>).</summary>
    public required string Actor { get; set; }

    /// <summary>Action identifier (for example <c>project.create</c>).</summary>
    public required string Action { get; set; }

    public required string TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>
    /// Project that owns the event, when applicable. Set explicitly at write time so key
    /// events remain filterable even though their target is an API key, not the project.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Optional JSON payload with non-secret contextual data.</summary>
    public string? Details { get; set; }

    public string? CorrelationId { get; set; }
}
