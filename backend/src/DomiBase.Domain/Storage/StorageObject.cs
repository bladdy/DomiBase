namespace DomiBase.Domain.Storage;

/// <summary>
/// Metadata for an object stored in a bucket. The physical bytes live on the storage backend.
/// </summary>
public sealed class StorageObject
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid BucketId { get; set; }

    /// <summary>Object path inside the bucket, for example <c>avatars/user-1.png</c>.</summary>
    public required string Path { get; set; }

    public string? ContentType { get; set; }

    public long Size { get; set; }

    /// <summary>Hex-encoded SHA-256 of the stored content.</summary>
    public required string Etag { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public StorageBucket Bucket { get; set; } = null!;
}
