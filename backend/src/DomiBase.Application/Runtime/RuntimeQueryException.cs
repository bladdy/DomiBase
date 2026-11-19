namespace DomiBase.Application.Runtime;

/// <summary>
/// Runtime-facing failure with a safe HTTP status/code/message. Never carries SQL or
/// database internals.
/// </summary>
public sealed class RuntimeQueryException(
    int statusCode,
    string code,
    string message,
    string? hint = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string? Hint { get; } = hint;
}
