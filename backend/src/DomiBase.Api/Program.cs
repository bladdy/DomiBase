using System.Threading.RateLimiting;
using DomiBase.Api.ControlPlane;
using DomiBase.Api.Endpoints;
using DomiBase.Api.Http;
using DomiBase.Api.Security;
using DomiBase.Application.Backups;
using DomiBase.Application.Functions;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure;
using DomiBase.Infrastructure.Backups;
using DomiBase.Infrastructure.Configuration;
using DomiBase.Infrastructure.Functions;
using DomiBase.Infrastructure.Runtime;
using DomiBase.Infrastructure.Storage;
using DomiBase.Provisioning;
using DomiBase.Provisioning.Catalog;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Local development only: fill missing configuration from the nearest .env file.
// Real environment variables and appsettings always win.
builder.Configuration.AddDomiBaseDotEnv(builder.Environment.ContentRootPath);

builder.Services.AddDomiBaseInfrastructure(builder.Configuration);
builder.Services.AddDomiBaseProvisioning(builder.Configuration);
builder.Services.AddDomiBaseProjectCatalog();
builder.Services.AddDomiBaseAdminAuthentication(builder.Configuration);
builder.Services.AddDomiBaseControlPlane(builder.Configuration);

var runtimeOptions = RuntimeServiceCollectionExtensions.CreateRuntimeOptions(builder.Configuration);
builder.Services.AddDomiBaseRuntime(builder.Configuration, runtimeOptions);
builder.Services.AddDomiBaseStorage(builder.Configuration);
builder.Services.AddDomiBaseFunctions(builder.Configuration);
builder.Services.AddDomiBaseBackups(builder.Configuration);

builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Panel login is public but strictly bounded per client address.
    options.AddPolicy("admin-login", httpContext =>
    {
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // RuntimeResolutionMiddleware runs first: valid keys share one project partition,
    // while invalid or missing keys are limited per client address with default quotas.
    options.AddPolicy("runtime", httpContext =>
    {
        var resolution = httpContext.Items.TryGetValue(
            RuntimeRequestResolution.HttpItemKey,
            out var stored) &&
            stored is RuntimeRequestResolution candidate
                ? candidate
                : null;

        var project = resolution?.Result.Context;
        var permits = project?.RateLimitPermits ?? runtimeOptions.RateLimitPermits;
        var windowSeconds = project?.RateLimitWindowSeconds ?? runtimeOptions.RateLimitWindowSeconds;

        // Never include the API key value in a partition key or log. Limits are part of the
        // key so changing an override starts a fresh window instead of inheriting an old one.
        var partitionKey = project is not null
            ? RuntimeRateLimitPartition.ForProject(project.ProjectId, permits, windowSeconds)
            : RuntimeRateLimitPartition.ForAddress(httpContext.Connection.RemoteIpAddress?.ToString());

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ApiError.Create(
                StatusCodes.Status429TooManyRequests,
                "RATE_LIMITED",
                "Too many requests. Retry later.")
            .ExecuteAsync(context.HttpContext);
    };
});

const string RuntimeCorsPolicy = "runtime";
var allowedOrigins = builder.Configuration["DOMIBASE_CORS_ORIGINS"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy(RuntimeCorsPolicy, policy =>
    {
        if (allowedOrigins is { Length: > 0 })
        {
            policy.WithOrigins(allowedOrigins);
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Astro dev server default port. CORS is a browser control, not authorization.
            policy.WithOrigins("http://localhost:4321");
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors(RuntimeCorsPolicy);
app.UseDomiBaseAdminAuthentication();

var functionOptions = app.Services.GetRequiredService<FunctionOptions>();

app.UseMiddleware<RuntimeResolutionMiddleware>();
app.UseRateLimiter();

// Runtime payload limit: bounded request bodies keep a single request from exhausting memory.
app.Use(async (context, next) =>
{
    var isRuntimeData = context.Request.Path.StartsWithSegments("/rest/v1");
    var isFunctionInvocation = context.Request.Path.StartsWithSegments("/functions/v1");
    if (isRuntimeData || isFunctionInvocation)
    {
        var maxBodyBytes = isFunctionInvocation ? functionOptions.MaxInputBytes : runtimeOptions.MaxBodyBytes;
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxBodyBytes;
        }

        var length = context.Request.ContentLength;
        if (length is not null && length > maxBodyBytes)
        {
            await ApiError.Create(
                    StatusCodes.Status413PayloadTooLarge,
                    "PAYLOAD_TOO_LARGE",
                    "The request body is too large.")
                .ExecuteAsync(context);
            return;
        }
    }

    await next(context);
});

app.MapSystemEndpoints();
app.MapControlEndpoints();
app.MapSchemaControlEndpoints();
app.MapMigrationControlEndpoints();
app.MapSqlQueryControlEndpoints();
app.MapDataPreviewControlEndpoints();
app.MapDataEditorControlEndpoints();
app.MapExtensionControlEndpoints();
app.MapBackupControlEndpoints();
app.MapAuditControlEndpoints();
app.MapAdminAuthEndpoints();
app.MapAuthEndpoints();
app.MapRuntimeEndpoints();
app.MapStorageControlEndpoints();
app.MapStorageRuntimeEndpoints();
app.MapFunctionControlEndpoints();
app.MapFunctionRuntimeEndpoints();
// Limits the HTTP/SSE/WebSocket handshake, not individual WebSocket frames.
app.MapHub<DomiBase.Infrastructure.Realtime.RealtimeHub>("/realtime/v1")
    .RequireRateLimiting("runtime");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program
{
}
