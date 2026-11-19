using Microsoft.Extensions.Configuration;

namespace DomiBase.Infrastructure.Configuration;

/// <summary>
/// Minimal <c>.env</c> loader for local development. Real environment variables and
/// appsettings values always win; <c>.env</c> only fills keys that are not configured yet.
/// Files are never required: production deployments use environment variables.
/// </summary>
public static class DotEnv
{
    private const int MaxParentLevels = 8;

    public static void AddDomiBaseDotEnv(this ConfigurationManager configuration, string startDirectory, string fileName = ".env")
    {
        var path = Find(startDirectory, fileName);
        if (path is null)
        {
            return;
        }

        var values = ParseFile(path)
            .Where(kv => string.IsNullOrEmpty(configuration[kv.Key]))
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value);

        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
    }

    /// <summary>Walks up from <paramref name="startDirectory"/> looking for the file.</summary>
    public static string? Find(string startDirectory, string fileName = ".env")
    {
        var directory = new DirectoryInfo(startDirectory);

        for (var level = 0; directory is not null && level <= MaxParentLevels; level++)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Parses <c>KEY=VALUE</c> lines. Comments, blank lines and <c>export</c> prefixes are supported.</summary>
    public static IReadOnlyDictionary<string, string> ParseFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Parse(File.ReadLines(path), values);
        return values;
    }

    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Parse(lines, values);
        return values;
    }

    private static void Parse(IEnumerable<string> lines, Dictionary<string, string> values)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }
    }
}
