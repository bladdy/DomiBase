using System.Globalization;
using System.Text.RegularExpressions;
using DomiBase.Application.Projects;

namespace DomiBase.Application.Auditing;

/// <summary>
/// Bound, strictly validated filter for the read-only audit viewer. Every value arrives from
/// HTTP query parameters; nothing here is concatenated into SQL.
/// </summary>
public sealed record AuditQuery(
    Guid? ProjectId,
    string? Action,
    string? Actor,
    string? TargetType,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit,
    int Offset)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxOffset = 1_000_000;

    private static readonly Regex TokenPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "projectId", "action", "actor", "targetType", "from", "to", "limit", "offset"
    };

    public static ControlResult<AuditQuery> Parse(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var key in values.Keys)
        {
            if (!AllowedKeys.Contains(key))
            {
                return Invalid("The audit filter contains an unknown parameter.", $"Allowed parameters: {string.Join(", ", AllowedKeys)}.");
            }
        }

        Guid? projectId = null;
        if (TryValue(values, "projectId", out var rawProjectId))
        {
            if (!Guid.TryParse(rawProjectId, out var parsedProjectId))
            {
                return Invalid("The project filter is not a valid identifier.");
            }
            projectId = parsedProjectId;
        }

        var action = ParseToken(values, "action", out var actionError);
        if (actionError is not null)
        {
            return Invalid(actionError);
        }

        var actor = ParseToken(values, "actor", out var actorError);
        if (actorError is not null)
        {
            return Invalid(actorError);
        }

        var targetType = ParseToken(values, "targetType", out var targetError);
        if (targetError is not null)
        {
            return Invalid(targetError);
        }

        DateTimeOffset? from = null;
        if (TryValue(values, "from", out var rawFrom))
        {
            if (!TryParseDate(rawFrom, out var parsedFrom))
            {
                return Invalid("The 'from' date is not a valid timestamp.");
            }
            from = parsedFrom;
        }

        DateTimeOffset? to = null;
        if (TryValue(values, "to", out var rawTo))
        {
            if (!TryParseDate(rawTo, out var parsedTo))
            {
                return Invalid("The 'to' date is not a valid timestamp.");
            }
            to = parsedTo;
        }

        if (from is not null && to is not null && from > to)
        {
            return Invalid("The date range is inverted.", "'from' must be earlier than or equal to 'to'.");
        }

        var limit = DefaultLimit;
        if (TryValue(values, "limit", out var rawLimit))
        {
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxLimit)
            {
                return Invalid($"The page size must be between 1 and {MaxLimit}.");
            }
        }

        var offset = 0;
        if (TryValue(values, "offset", out var rawOffset))
        {
            if (!int.TryParse(rawOffset, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                || offset < 0 || offset > MaxOffset)
            {
                return Invalid($"The offset must be between 0 and {MaxOffset}.");
            }
        }

        return ControlResult<AuditQuery>.Ok(
            new AuditQuery(projectId, action, actor, targetType, from, to, limit, offset));
    }

    private static string? ParseToken(
        IReadOnlyDictionary<string, string?> values,
        string key,
        out string? error)
    {
        error = null;
        if (!TryValue(values, key, out var raw))
        {
            return null;
        }

        if (!TokenPattern.IsMatch(raw))
        {
            error = $"The '{key}' filter accepts only letters, digits, '.', '_', ':' and '-' (up to 64 characters).";
            return null;
        }

        return raw;
    }

    private static bool TryValue(IReadOnlyDictionary<string, string?> values, string key, out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return value.Length > 0;
    }

    private static bool TryParseDate(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static ControlResult<AuditQuery> Invalid(string message, string? hint = null) =>
        ControlResult<AuditQuery>.Fail(ControlErrorCode.Validation, message, hint);
}
