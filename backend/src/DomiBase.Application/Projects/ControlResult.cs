namespace DomiBase.Application.Projects;

public enum ControlErrorCode
{
    Validation,
    NotFound,
    Conflict
}

public sealed record ControlError(ControlErrorCode Code, string Message, string? Hint = null);

/// <summary>
/// Result of a Control Plane operation. Keeps transport concerns (HTTP status codes)
/// out of the application layer while still allowing precise error mapping.
/// </summary>
public sealed record ControlResult<T>(T? Value, ControlError? Error)
{
    public bool Succeeded => Error is null;

    public static ControlResult<T> Ok(T value) => new(value, null);

    public static ControlResult<T> Fail(ControlErrorCode code, string message, string? hint = null) =>
        new(default, new ControlError(code, message, hint));
}
