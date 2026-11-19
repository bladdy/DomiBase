namespace DomiBase.Application.Storage;

public sealed record StorageOptions
{
    public required string RootDirectory { get; init; }

    public required long MaxObjectSizeBytes { get; init; }

    public required string SigningKey { get; init; }
}
