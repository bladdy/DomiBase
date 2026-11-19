namespace DomiBase.Domain.Projects;

/// <summary>
/// Metadata for a DomiBase project. Each project owns an independent PostgreSQL database.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>URL/CLI friendly name, unique across the platform.</summary>
    public required string Slug { get; set; }

    /// <summary>Human readable display name.</summary>
    public required string Name { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Provisioning;

    /// <summary>Physical database name derived from <see cref="Slug"/>.</summary>
    public required string DatabaseName { get; set; }

    /// <summary>Last provisioning/operation failure summary, if any.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// AES-GCM protected password for the project's runtime PostgreSQL login.
    /// Only the provisioning and Runtime pipelines decrypt it; it is never exposed by the Control API.
    /// </summary>
    public string? RuntimePasswordCiphertext { get; set; }

    /// <summary>
    /// Runtime requests allowed per window for this project. <see langword="null"/> inherits
    /// the platform default; the Runtime resolver computes the effective value.
    /// </summary>
    public int? RateLimitPermits { get; set; }

    /// <summary>
    /// Length in seconds of the project's rate-limit window. <see langword="null"/> inherits
    /// the platform default.
    /// </summary>
    public int? RateLimitWindowSeconds { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();

    public ICollection<ProvisioningOperation> ProvisioningOperations { get; set; } = new List<ProvisioningOperation>();

    public ICollection<ProjectAuthKey> AuthSigningKeys { get; set; } = new List<ProjectAuthKey>();
}
