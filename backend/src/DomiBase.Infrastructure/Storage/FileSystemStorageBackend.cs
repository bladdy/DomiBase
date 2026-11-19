using System.Security.Cryptography;
using DomiBase.Application.Storage;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Storage;

/// <summary>
/// Local filesystem storage backend for development. Layout:
/// <c>{root}/{projectId}/{bucket}/{objectPath}</c>.
/// </summary>
public sealed class FileSystemStorageBackend : IStorageBackend
{
    private readonly string _root;
    private readonly ILogger<FileSystemStorageBackend> _logger;

    public FileSystemStorageBackend(StorageOptions options, ILogger<FileSystemStorageBackend> logger)
    {
        _root = options.RootDirectory;
        _logger = logger;

        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
        }
    }

    public async Task<StorageWriteResult> WriteAsync(
        Guid projectId,
        string bucket,
        string path,
        Stream content,
        long maxSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(projectId, bucket, path);
        var directory = Path.GetDirectoryName(filePath)!;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            long size;
            string etag;

            using (var fileStream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.Asynchronous))
            using (var sha256 = SHA256.Create())
            using (var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write))
            {
                size = await CopyWithLimitAsync(content, hashingStream, maxSizeBytes, cancellationToken);
                await hashingStream.FlushFinalBlockAsync(cancellationToken);
                etag = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            }

            File.Move(tempPath, filePath, overwrite: true);
            return new StorageWriteResult(etag, size);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxSizeBytes)
            {
                throw new StorageException(
                    "OBJECT_TOO_LARGE",
                    $"Object exceeds the maximum size of {maxSizeBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }

    public Task<Stream?> ReadAsync(
        Guid projectId,
        string bucket,
        string path,
        CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(projectId, bucket, path);

        if (!File.Exists(filePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DeleteAsync(
        Guid projectId,
        string bucket,
        string path,
        CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(projectId, bucket, path);

        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(filePath);
            DeleteEmptyDirectories(Path.GetDirectoryName(filePath)!);
            return Task.FromResult(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to delete storage object {ProjectId}/{Bucket}/{Path}.",
                projectId,
                bucket,
                path);
            return Task.FromResult(false);
        }
    }

    private string ResolvePath(Guid projectId, string bucket, string path)
    {
        // Prevent directory traversal: reject any path segment that escapes the bucket.
        var fullPath = Path.GetFullPath(Path.Combine(_root, projectId.ToString("N"), bucket, path));
        var bucketRoot = Path.GetFullPath(Path.Combine(_root, projectId.ToString("N"), bucket));

        if (!fullPath.StartsWith(bucketRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !fullPath.Equals(bucketRoot, StringComparison.Ordinal))
        {
            throw new StorageException("INVALID_OBJECT_PATH", "Object path is invalid.");
        }

        return fullPath;
    }

    private static void DeleteEmptyDirectories(string directory)
    {
        while (directory.Length > 0 && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory) ?? string.Empty;
        }
    }
}
