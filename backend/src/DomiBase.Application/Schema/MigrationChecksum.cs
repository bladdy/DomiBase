using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DomiBase.Application.Schema;

/// <summary>
/// Produces a stable fingerprint of a migration's operations. Each operation is serialized
/// through its concrete runtime type so every field participates in the hash. The result is
/// independent of collection instances and process restarts.
/// </summary>
public static class MigrationChecksum
{
    public const string VersionPrefix = "v1.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Compute(IReadOnlyList<SchemaMutation> operations)
    {
        var canonical = string.Join(
            "\n",
            operations.Select(operation => JsonSerializer.Serialize((object)operation, Options)));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("domibase-migration-v1\n" + canonical));
        return VersionPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
