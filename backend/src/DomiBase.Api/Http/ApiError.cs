namespace DomiBase.Api.Http;

/// <summary>
/// Builds the canonical error envelope:
/// <c>{ "error": { "code", "message", "details"?, "hint"? } }</c>.
/// Never include secrets, SQL or stack traces.
/// </summary>
public static class ApiError
{
    public static IResult Create(
        int statusCode,
        string code,
        string message,
        string? details = null,
        string? hint = null)
    {
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        };

        if (!string.IsNullOrWhiteSpace(details))
        {
            error["details"] = details;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            error["hint"] = hint;
        }

        return Results.Json(new Dictionary<string, object?> { ["error"] = error }, statusCode: statusCode);
    }

    /// <summary>Honest placeholder used while a plane is not implemented yet.</summary>
    public static IResult NotImplemented(string feature, string? hint = null) =>
        Create(
            StatusCodes.Status501NotImplemented,
            "NOT_IMPLEMENTED",
            $"{feature} is not implemented yet.",
            hint: hint ?? "This endpoint exists as a contract placeholder and will be implemented in a later phase.");
}
