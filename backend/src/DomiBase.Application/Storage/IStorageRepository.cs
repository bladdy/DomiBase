using DomiBase.Domain.Storage;

namespace DomiBase.Application.Storage;

/// <summary>
/// Control Plane metadata operations for project buckets and objects.
/// </summary>
public interface IStorageRepository
{
    Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<StorageBucket?> FindBucketAsync(Guid projectId, string name, CancellationToken cancellationToken = default);

    Task<StorageBucket?> GetBucketAsync(Guid bucketId, CancellationToken cancellationToken = default);

    Task<StorageBucket> CreateBucketAsync(StorageBucket bucket, CancellationToken cancellationToken = default);

    Task<bool> DeleteBucketAsync(Guid projectId, Guid bucketId, CancellationToken cancellationToken = default);

    Task<StorageObject?> FindObjectAsync(Guid bucketId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageObject>> ListObjectsAsync(
        Guid bucketId,
        string? prefix,
        CancellationToken cancellationToken = default);

    Task<StorageObject> UpsertObjectAsync(StorageObject obj, CancellationToken cancellationToken = default);

    Task<bool> DeleteObjectAsync(Guid bucketId, string path, CancellationToken cancellationToken = default);
}
