using System.Security.Cryptography;
using System.Text;
using System.Web;
using DomiBase.Domain.Storage;

namespace DomiBase.Application.Storage;

/// <summary>
/// Project-scoped storage orchestration. Authorization is based on the bucket's
/// public/private flag and the caller's authenticated/anonymous role.
/// </summary>
public sealed class StorageService(
    IStorageRepository repository,
    IStorageBackend backend,
    StorageOptions options)
{
    private const int MaxBucketNameLength = 40;
    private const int MaxObjectPathLength = 1024;

    public async Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await repository.ListBucketsAsync(projectId, cancellationToken);

    public async Task<StorageBucket?> GetBucketAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default) =>
        await repository.FindBucketAsync(projectId, name, cancellationToken);

    public async Task<StorageResult<StorageBucket>> CreateBucketAsync(
        Guid projectId,
        string name,
        bool publicAccess,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBucketName(name);
        if (normalized.Succeeded == false)
        {
            return StorageResult<StorageBucket>.Fail(normalized.Code, normalized.Message);
        }

        var existing = await repository.FindBucketAsync(projectId, normalized.Value!, cancellationToken);
        if (existing is not null)
        {
            return StorageResult<StorageBucket>.Fail("BUCKET_ALREADY_EXISTS", "A bucket with this name already exists.");
        }

        var bucket = new StorageBucket
        {
            ProjectId = projectId,
            Name = normalized.Value!,
            PublicAccess = publicAccess
        };

        await repository.CreateBucketAsync(bucket, cancellationToken);
        return StorageResult<StorageBucket>.Ok(bucket);
    }

    public async Task<StorageResult<bool>> DeleteBucketAsync(
        Guid projectId,
        Guid bucketId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await repository.DeleteBucketAsync(projectId, bucketId, cancellationToken);
        return deleted
            ? StorageResult<bool>.Ok(true)
            : StorageResult<bool>.Fail("BUCKET_NOT_FOUND", "Bucket not found.");
    }

    public async Task<StorageResult<IReadOnlyList<StorageObject>>> ListObjectsAsync(
        Guid projectId,
        string bucketName,
        string? prefix,
        bool isAuthenticated,
        CancellationToken cancellationToken = default)
    {
        var bucket = await repository.FindBucketAsync(projectId, bucketName, cancellationToken);
        if (bucket is null)
        {
            return StorageResult<IReadOnlyList<StorageObject>>.Fail("BUCKET_NOT_FOUND", "Bucket not found.");
        }

        if (!CanRead(bucket, isAuthenticated))
        {
            return StorageResult<IReadOnlyList<StorageObject>>.Fail("FORBIDDEN", "Not authorized to list this bucket.");
        }

        var objects = await repository.ListObjectsAsync(bucket.Id, prefix, cancellationToken);
        return StorageResult<IReadOnlyList<StorageObject>>.Ok(objects);
    }

    public async Task<StorageResult<StorageObject>> UploadAsync(
        Guid projectId,
        string bucketName,
        string path,
        string? contentType,
        Stream content,
        bool isAuthenticated,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeObjectPath(path);
        if (normalizedPath.Succeeded == false)
        {
            return StorageResult<StorageObject>.Fail(normalizedPath.Code, normalizedPath.Message);
        }

        var bucket = await repository.FindBucketAsync(projectId, bucketName, cancellationToken);
        if (bucket is null)
        {
            return StorageResult<StorageObject>.Fail("BUCKET_NOT_FOUND", "Bucket not found.");
        }

        if (!CanWrite(bucket, isAuthenticated))
        {
            return StorageResult<StorageObject>.Fail("FORBIDDEN", "Not authorized to upload to this bucket.");
        }

        StorageWriteResult writeResult;
        try
        {
            writeResult = await backend.WriteAsync(
                projectId,
                bucket.Name,
                normalizedPath.Value!,
                content,
                options.MaxObjectSizeBytes,
                cancellationToken);
        }
        catch (StorageException exception)
        {
            return StorageResult<StorageObject>.Fail(exception.Code, exception.Message);
        }

        var obj = new StorageObject
        {
            BucketId = bucket.Id,
            Path = normalizedPath.Value!,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? InferContentType(normalizedPath.Value!) : contentType,
            Size = writeResult.Size,
            Etag = writeResult.Etag
        };

        await repository.UpsertObjectAsync(obj, cancellationToken);
        return StorageResult<StorageObject>.Ok(obj);
    }

    public async Task<StorageResult<Stream?>> DownloadAsync(
        Guid projectId,
        string bucketName,
        string path,
        bool isAuthenticated,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeObjectPath(path);
        if (normalizedPath.Succeeded == false)
        {
            return StorageResult<Stream?>.Fail(normalizedPath.Code, normalizedPath.Message);
        }

        var bucket = await repository.FindBucketAsync(projectId, bucketName, cancellationToken);
        if (bucket is null)
        {
            return StorageResult<Stream?>.Fail("BUCKET_NOT_FOUND", "Bucket not found.");
        }

        if (!CanRead(bucket, isAuthenticated))
        {
            return StorageResult<Stream?>.Fail("FORBIDDEN", "Not authorized to read this bucket.");
        }

        var obj = await repository.FindObjectAsync(bucket.Id, normalizedPath.Value!, cancellationToken);
        if (obj is null)
        {
            return StorageResult<Stream?>.Fail("OBJECT_NOT_FOUND", "Object not found.");
        }

        var stream = await backend.ReadAsync(projectId, bucket.Name, normalizedPath.Value!, cancellationToken);
        if (stream is null)
        {
            return StorageResult<Stream?>.Fail("OBJECT_NOT_FOUND", "Object not found.");
        }

        return StorageResult<Stream?>.Ok(stream, obj);
    }

    public async Task<StorageResult<bool>> DeleteAsync(
        Guid projectId,
        string bucketName,
        string path,
        bool isAuthenticated,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeObjectPath(path);
        if (normalizedPath.Succeeded == false)
        {
            return StorageResult<bool>.Fail(normalizedPath.Code, normalizedPath.Message);
        }

        var bucket = await repository.FindBucketAsync(projectId, bucketName, cancellationToken);
        if (bucket is null)
        {
            return StorageResult<bool>.Fail("BUCKET_NOT_FOUND", "Bucket not found.");
        }

        if (!CanWrite(bucket, isAuthenticated))
        {
            return StorageResult<bool>.Fail("FORBIDDEN", "Not authorized to delete from this bucket.");
        }

        var deletedMeta = await repository.DeleteObjectAsync(bucket.Id, normalizedPath.Value!, cancellationToken);
        var deletedBytes = await backend.DeleteAsync(projectId, bucket.Name, normalizedPath.Value!, cancellationToken);

        if (!deletedMeta && !deletedBytes)
        {
            return StorageResult<bool>.Fail("OBJECT_NOT_FOUND", "Object not found.");
        }

        return StorageResult<bool>.Ok(true);
    }

    public StorageResult<string> CreateSignedUrl(
        Guid projectId,
        string bucketName,
        string path,
        TimeSpan expiresIn)
    {
        var normalizedPath = NormalizeObjectPath(path);
        if (normalizedPath.Succeeded == false)
        {
            return StorageResult<string>.Fail(normalizedPath.Code, normalizedPath.Message);
        }

        if (expiresIn <= TimeSpan.Zero || expiresIn > TimeSpan.FromDays(7))
        {
            return StorageResult<string>.Fail("INVALID_EXPIRATION", "Expiration must be between 1 second and 7 days.");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(expiresIn);
        var signature = Sign(projectId, bucketName, normalizedPath.Value!, expiresAt);

        var builder = new StringBuilder();
        builder.Append($"/storage/v1/{HttpUtility.UrlEncode(bucketName)}/{normalizedPath.Value!}");
        builder.Append($"?expires={expiresAt.ToUnixTimeSeconds()}");
        builder.Append($"&signature={signature}");

        return StorageResult<string>.Ok(builder.ToString());
    }

    public StorageResult<bool> ValidateSignedUrl(
        Guid projectId,
        string bucketName,
        string path,
        long expiresUnix,
        string signature)
    {
        if (expiresUnix <= 0)
        {
            return StorageResult<bool>.Fail("INVALID_SIGNATURE", "The signed URL signature is invalid.");
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return StorageResult<bool>.Fail("SIGNED_URL_EXPIRED", "The signed URL has expired.");
        }

        var expected = Sign(projectId, bucketName, path, expiresAt);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature)))
        {
            return StorageResult<bool>.Fail("INVALID_SIGNATURE", "The signed URL signature is invalid.");
        }

        return StorageResult<bool>.Ok(true);
    }

    private static bool CanRead(StorageBucket bucket, bool isAuthenticated) =>
        bucket.PublicAccess || isAuthenticated;

    private static bool CanWrite(StorageBucket bucket, bool isAuthenticated) =>
        isAuthenticated;

    private static StorageResult<string> NormalizeBucketName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxBucketNameLength)
        {
            return StorageResult<string>.Fail("INVALID_BUCKET_NAME", "Bucket name must be between 1 and 40 characters.");
        }

        var normalized = name.Trim().ToLowerInvariant();

        if (!normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return StorageResult<string>.Fail("INVALID_BUCKET_NAME", "Bucket name may only contain lowercase letters, digits and hyphens.");
        }

        if (normalized.StartsWith('-') || normalized.EndsWith('-'))
        {
            return StorageResult<string>.Fail("INVALID_BUCKET_NAME", "Bucket name must not start or end with a hyphen.");
        }

        return StorageResult<string>.Ok(normalized);
    }

    private static StorageResult<string> NormalizeObjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxObjectPathLength)
        {
            return StorageResult<string>.Fail("INVALID_OBJECT_PATH", "Object path is invalid.");
        }

        var normalized = path.Trim().Replace('\\', '/').TrimStart('/');

        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return StorageResult<string>.Fail("INVALID_OBJECT_PATH", "Object path cannot contain parent references.");
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return StorageResult<string>.Fail("INVALID_OBJECT_PATH", "Object path is invalid.");
        }

        return StorageResult<string>.Ok(normalized);
    }

    private static string InferContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }

    private string Sign(Guid projectId, string bucket, string path, DateTimeOffset expiresAt)
    {
        var payload = $"{projectId:N}:{bucket}:{path}:{expiresAt.ToUnixTimeSeconds()}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.SigningKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class StorageResult<T>
{
    public bool Succeeded { get; }

    public T? Value { get; }

    public StorageObject? Object { get; }

    public string Code { get; }

    public string Message { get; }

    private StorageResult(T? value, StorageObject? obj, bool succeeded, string code, string message)
    {
        Value = value;
        Object = obj;
        Succeeded = succeeded;
        Code = code;
        Message = message;
    }

    public static StorageResult<T> Ok(T value, StorageObject? obj = null) =>
        new(value, obj, true, string.Empty, string.Empty);

    public static StorageResult<T> Fail(string code, string message) =>
        new(default, null, false, code, message);
}
