using DomiBase.Application.Storage;
using DomiBase.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace DomiBase.Infrastructure.Persistence;

public sealed class EfStorageRepository(DomiBaseDbContext db) : IStorageRepository
{
    public async Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.StorageBuckets
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.Name)
            .ToListAsync(cancellationToken);

    public async Task<StorageBucket?> FindBucketAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default) =>
        await db.StorageBuckets
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.Name == name, cancellationToken);

    public async Task<StorageBucket?> GetBucketAsync(
        Guid bucketId,
        CancellationToken cancellationToken = default) =>
        await db.StorageBuckets
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bucketId, cancellationToken);

    public async Task<StorageBucket> CreateBucketAsync(
        StorageBucket bucket,
        CancellationToken cancellationToken = default)
    {
        db.StorageBuckets.Add(bucket);
        await db.SaveChangesAsync(cancellationToken);
        return bucket;
    }

    public async Task<bool> DeleteBucketAsync(
        Guid projectId,
        Guid bucketId,
        CancellationToken cancellationToken = default)
    {
        var bucket = await db.StorageBuckets
            .FirstOrDefaultAsync(b => b.Id == bucketId && b.ProjectId == projectId, cancellationToken);

        if (bucket is null)
        {
            return false;
        }

        db.StorageBuckets.Remove(bucket);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<StorageObject?> FindObjectAsync(
        Guid bucketId,
        string path,
        CancellationToken cancellationToken = default) =>
        await db.StorageObjects
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.BucketId == bucketId && o.Path == path, cancellationToken);

    public async Task<IReadOnlyList<StorageObject>> ListObjectsAsync(
        Guid bucketId,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        var query = db.StorageObjects
            .AsNoTracking()
            .Where(o => o.BucketId == bucketId);

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            query = query.Where(o => o.Path.StartsWith(prefix));
        }

        return await query
            .OrderBy(o => o.Path)
            .ToListAsync(cancellationToken);
    }

    public async Task<StorageObject> UpsertObjectAsync(
        StorageObject obj,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.StorageObjects
            .FirstOrDefaultAsync(o => o.BucketId == obj.BucketId && o.Path == obj.Path, cancellationToken);

        if (existing is null)
        {
            db.StorageObjects.Add(obj);
        }
        else
        {
            existing.ContentType = obj.ContentType;
            existing.Size = obj.Size;
            existing.Etag = obj.Etag;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return obj;
    }

    public async Task<bool> DeleteObjectAsync(
        Guid bucketId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var obj = await db.StorageObjects
            .FirstOrDefaultAsync(o => o.BucketId == bucketId && o.Path == path, cancellationToken);

        if (obj is null)
        {
            return false;
        }

        db.StorageObjects.Remove(obj);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
