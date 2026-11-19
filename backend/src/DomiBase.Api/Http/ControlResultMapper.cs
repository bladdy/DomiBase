using DomiBase.Application.Projects;

namespace DomiBase.Api.Http;

/// <summary>Maps application results to the canonical HTTP error envelope.</summary>
public static class ControlResultMapper
{
    public static IResult ToHttp<T>(ControlResult<T> result, Func<T, IResult> onSuccess)
    {
        if (result.Succeeded && result.Value is not null)
        {
            return onSuccess(result.Value);
        }

        var error = result.Error!;

        return error.Code switch
        {
            ControlErrorCode.Validation =>
                ApiError.Create(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", error.Message, hint: error.Hint),
            ControlErrorCode.NotFound =>
                ApiError.Create(StatusCodes.Status404NotFound, "NOT_FOUND", error.Message, hint: error.Hint),
            ControlErrorCode.Conflict =>
                ApiError.Create(StatusCodes.Status409Conflict, "CONFLICT", error.Message, hint: error.Hint),
            _ => ApiError.Create(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.")
        };
    }
}
