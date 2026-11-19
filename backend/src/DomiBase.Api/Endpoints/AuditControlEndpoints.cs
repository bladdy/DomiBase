using DomiBase.Api.Http;
using DomiBase.Application.Auditing;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Read-only Control Plane views over <c>audit_entries</c> and <c>provisioning_operations</c>.
/// Protected by the administrative middleware and never mapped under <c>/rest/v1</c>.
/// </summary>
public static class AuditControlEndpoints
{
    public static IEndpointRouteBuilder MapAuditControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", async (
                HttpContext http,
                AuditService service,
                CancellationToken cancellationToken) =>
            {
                var values = http.Request.Query.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Count > 0 ? pair.Value[^1] : null,
                    StringComparer.OrdinalIgnoreCase);

                var query = AuditQuery.Parse(values);
                if (!query.Succeeded || query.Value is null)
                {
                    return ControlResultMapper.ToHttp(query, _ => Results.Ok());
                }

                var result = await service.ListAsync(query.Value, cancellationToken);
                return ControlResultMapper.ToHttp(result, page => Results.Ok(page));
            })
            .WithName("ControlListAuditEntries");

        app.MapGet("/api/v1/projects/{projectId:guid}/provisioning-operations", async (
                Guid projectId,
                AuditService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ListProvisioningAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(result, operations => Results.Ok(operations));
            })
            .WithName("ControlListProvisioningOperations");

        return app;
    }
}
