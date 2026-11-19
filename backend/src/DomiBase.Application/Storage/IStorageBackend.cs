namespace DomiBase.Application.Storage;

/// <summary>
/// Pluggable storage backend. Implementations handle bytes only; metadata lives
/// in the control database.
/// </summary>
public interface IStorageBackend
{
    Task<StorageWriteResult> WriteAsync(
        Guid projectId,
        string bucket,
        string path,
        Stream content,
        long maxSizeBytes,
        CancellationToken cancellationToken = default);

    Task<Stream?> ReadAsync(
        Guid projectId,
        string bucket,
        string path,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid projectId,
        string bucket,
        string path,
        CancellationToken cancellationToken = default);
}

public sealed record StorageWriteResult(string Etag, long Size);

public sealed class StorageException : Exception
{
    public StorageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
