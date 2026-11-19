namespace DomiBase.Domain.Functions;

/// <summary>
/// A WebAssembly function registered for a project. The module bytes and metadata
/// live in the control database; they are sent to an isolated worker for execution.
/// </summary>
public sealed class FunctionDefinition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>Function name: lowercase letters, digits, hyphens and underscores, 1-63 chars.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Raw WebAssembly module bytes. Never returned by any endpoint.</summary>
    public required byte[] Module { get; set; }

    /// <summary>Hex-encoded SHA-256 of <see cref="Module"/>.</summary>
    public required string ModuleSha256 { get; set; }

    public bool Enabled { get; set; } = true;

    public long Fuel { get; set; } = 1_000_000;

    public int MemoryPages { get; set; } = 16;

    public int TimeoutMs { get; set; } = 1_000;

    public int MaxInputBytes { get; set; } = 65_536;

    public int MaxOutputBytes { get; set; } = 65_536;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
