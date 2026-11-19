using DomiBase.Api.Contracts;
using DomiBase.Api.Http;
using DomiBase.Application.Projects;
using DomiBase.Application.Storage;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints for project storage buckets.
/// </summary>
public static class StorageControlEndpoints
{
    public static IEndpointRouteBuilder MapStorageControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}/buckets").WithTags("Storage Control");

        group.MapGet("", async (Guid projectId, StorageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListBucketsAsync(projectId, cancellationToken);
            return Results.Ok(result.Select(BucketResponse.From));
        });

        group.MapPost("", async (Guid projectId, CreateBucketRequest request, StorageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateBucketAsync(projectId, request.Name, request.PublicAccess, cancellationToken);
            return MapResult(result, bucket => Results.Created($"/api/v1/projects/{projectId}/buckets/{bucket.Id}", BucketResponse.From(bucket)));
        });

        group.MapDelete("/{bucketId:guid}", async (Guid projectId, Guid bucketId, StorageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DeleteBucketAsync(projectId, bucketId, cancellationToken);
            return MapResult(result, _ => Results.NoContent());
        });

        return app;
    }

    private static IResult MapResult<T>(StorageResult<T> result, Func<T, IResult> onSuccess)
    {
        if (!result.Succeeded || result.Value is null)
        {
            var status = result.Code switch
            {
                "BUCKET_NOT_FOUND" => StatusCodes.Status404NotFound,
                "BUCKET_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };

            return ApiError.Create(status, result.Code, result.Message);
        }

        return onSuccess(result.Value);
    }
}

public sealed record CreateBucketRequest(string Name, bool PublicAccess);

public sealed record BucketResponse(Guid Id, string Name, bool PublicAccess, DateTimeOffset CreatedAt)
{
    public static BucketResponse From(Domain.Storage.StorageBucket bucket) => new(
        bucket.Id,
        bucket.Name,
        bucket.PublicAccess,
        bucket.CreatedAt);
}
