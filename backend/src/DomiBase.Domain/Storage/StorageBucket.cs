namespace DomiBase.Domain.Storage;

/// <summary>
/// A bucket within a project. Buckets are stored in the control database; their
/// objects are referenced by metadata here but stored on the configured storage backend.
/// </summary>
public sealed class StorageBucket
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>Bucket name: lowercase letters, digits and hyphens, 3-40 chars.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// When true, anonymous clients can read/list objects. Write/delete still requires
    /// an authenticated user.
    /// </summary>
    public bool PublicAccess { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<StorageObject> Objects { get; set; } = new List<StorageObject>();
}
