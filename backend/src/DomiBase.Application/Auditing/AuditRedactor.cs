using System.Text.RegularExpressions;

namespace DomiBase.Application.Auditing;

/// <summary>
/// Defense in depth for the audit viewer. Writers must never store secrets, but older or
/// hand-written rows could; these patterns mask the known secret shapes before serialization.
/// </summary>
public static partial class AuditRedactor
{
    private const string Mask = "[redacted]";
    private const int MaxLength = 4000;

    [GeneratedRegex(
        "\"(?<name>[A-Za-z0-9_-]*(?:password|passwd|secret|token|api[_-]?key|apikey|authorization|credential)[A-Za-z0-9_-]*)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"\bdb_(?:public|secret)_[A-Za-z0-9_-]{6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\b(password|passwd|pgpassword|api[_-]?key|token|secret)\s*[:=]\s*[^\s,;""'}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    public static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = JsonSecret().Replace(value, match =>
            $"\"{match.Groups["name"].Value}\":\"{Mask}\"");
        redacted = ApiKey().Replace(redacted, Mask);
        redacted = Assignment().Replace(redacted, $"$1={Mask}");
        redacted = Jwt().Replace(redacted, Mask);

        return redacted.Length <= MaxLength
            ? redacted
            : $"{redacted[..MaxLength]}…";
    }
}
