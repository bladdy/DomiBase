using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// System endpoints shared by both planes.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (IHostEnvironment environment) => Results.Json(new
            {
                status = "ok",
                service = "domibase-api",
                environment = environment.EnvironmentName,
                timestamp = DateTimeOffset.UtcNow
            }))
            .WithName("Health")
            .WithTags("System");

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = WriteReadinessAsync
            })
            .WithTags("System");

        return app;
    }

    private static async Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        var payload = new
        {
            status = report.Status == HealthStatus.Healthy ? "ready" : "unavailable",
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString().ToLowerInvariant() })
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(payload);
    }
}
