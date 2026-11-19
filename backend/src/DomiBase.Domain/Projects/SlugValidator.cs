using System.Text.RegularExpressions;

namespace DomiBase.Domain.Projects;

/// <summary>
/// Validates and normalizes project slugs. Slugs are lowercase,
/// start with a letter and contain only a-z, 0-9 and single hyphens.
/// </summary>
public static partial class SlugValidator
{
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal)
    {
        "postgres",
        "template0",
        "template1",
        "domibase",
        "domibase_control",
        "control",
        "admin",
        "runtime",
        "public",
        "information_schema"
    };

    public static bool IsValid(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (slug.Length > ProjectNaming.MaxSlugLength) return false;
        if (ReservedSlugs.Contains(slug)) return false;
        return SlugPattern().IsMatch(slug);
    }

    /// <summary>
    /// Normalizes user input (trim, lowercase, spaces/underscores to hyphens)
    /// and validates the result.
    /// </summary>
    public static bool TryNormalize(string? input, out string slug, out string? error)
    {
        slug = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Slug is required.";
            return false;
        }

        var candidate = input.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        while (candidate.Contains("--", StringComparison.Ordinal))
        {
            candidate = candidate.Replace("--", "-", StringComparison.Ordinal);
        }

        candidate = candidate.Trim('-');

        if (!IsValid(candidate))
        {
            error =
                $"Slug '{candidate}' is invalid. Use 1-{ProjectNaming.MaxSlugLength} lowercase letters, " +
                "digits and single hyphens, starting with a letter, and avoid reserved names.";
            return false;
        }

        slug = candidate;
        error = null;
        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
