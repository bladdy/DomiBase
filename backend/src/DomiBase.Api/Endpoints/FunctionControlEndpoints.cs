using DomiBase.Api.Http;
using DomiBase.Application.Functions;

namespace DomiBase.Api.Endpoints;

/// <summary>Control Plane endpoints for project function definitions.</summary>
public static class FunctionControlEndpoints
{
    public static IEndpointRouteBuilder MapFunctionControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}/functions").WithTags("Functions Control");

        group.MapGet("", async (Guid projectId, FunctionService service, CancellationToken cancellationToken) =>
        {
            var functions = await service.ListAsync(projectId, cancellationToken);
            return Results.Ok(functions.Select(FunctionResponse.From));
        });

        group.MapPost("", async (
            Guid projectId,
            CreateFunctionRequest request,
            FunctionService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(
                projectId,
                request.Name,
                request.Description,
                request.ModuleBase64,
                request.Limits is null
                    ? null
                    : new FunctionLimitInput
                    {
                        Fuel = request.Limits.Fuel,
                        MemoryPages = request.Limits.MemoryPages,
                        TimeoutMs = request.Limits.TimeoutMs,
                        MaxInputBytes = request.Limits.MaxInputBytes,
                        MaxOutputBytes = request.Limits.MaxOutputBytes
                    },
                cancellationToken);

            return MapResult(result, function => Results.Created(
                $"/api/v1/projects/{projectId}/functions/{function.Id}",
                FunctionResponse.From(function)));
        });

        group.MapDelete("/{functionId:guid}", async (
            Guid projectId,
            Guid functionId,
            FunctionService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteAsync(projectId, functionId, cancellationToken);
            return MapResult(result, _ => Results.NoContent());
        });

        return app;
    }

    private static IResult MapResult<T>(FunctionResult<T> result, Func<T, IResult> onSuccess)
    {
        if (!result.Succeeded || result.Value is null)
        {
            var status = result.Code switch
            {
                "FUNCTION_NOT_FOUND" => StatusCodes.Status404NotFound,
                "FUNCTION_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
                "INVALID_FUNCTION_NAME" or "INVALID_DESCRIPTION" or "INVALID_MODULE"
                    or "MODULE_TOO_LARGE" or "INVALID_LIMITS" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status400BadRequest
            };

            return ApiError.Create(status, result.Code, result.Message);
        }

        return onSuccess(result.Value);
    }
}

public sealed record CreateFunctionRequest(
    string Name,
    string? Description,
    string ModuleBase64,
    FunctionLimitRequest? Limits);

public sealed record FunctionLimitRequest
{
    public long? Fuel { get; init; }
    public int? MemoryPages { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxInputBytes { get; init; }
    public int? MaxOutputBytes { get; init; }
}

public sealed record FunctionResponse(
    Guid Id,
    string Name,
    string? Description,
    string ModuleSha256,
    bool Enabled,
    long Fuel,
    int MemoryPages,
    int TimeoutMs,
    int MaxInputBytes,
    int MaxOutputBytes,
    DateTimeOffset CreatedAt)
{
    public static FunctionResponse From(Domain.Functions.FunctionDefinition function) => new(
        function.Id,
        function.Name,
        function.Description,
        function.ModuleSha256,
        function.Enabled,
        function.Fuel,
        function.MemoryPages,
        function.TimeoutMs,
        function.MaxInputBytes,
        function.MaxOutputBytes,
        function.CreatedAt);
}
