using DomiBase.Application.Projects;

namespace DomiBase.Application.Auditing;

public sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string TargetType,
    string? TargetId,
    Guid? ProjectId,
    string? Details,
    string? CorrelationId);

public sealed record AuditPageResponse(
    IReadOnlyList<AuditEntryResponse> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record ProvisioningOperationResponse(
    Guid Id,
    Guid ProjectId,
    string Step,
    string Status,
    int Attempt,
    string? Error,
    string? CorrelationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Read-only projection of the control database audit trail and provisioning steps. It never
/// accepts SQL, filesystem paths or credentials and never writes an audit row for its own reads.
/// </summary>
public sealed class AuditService(IProjectRepository repository)
{
    public async Task<ControlResult<AuditPageResponse>> ListAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.ListAuditEntriesAsync(query, cancellationToken);

        return ControlResult<AuditPageResponse>.Ok(new AuditPageResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.Total,
            query.Limit,
            query.Offset));
    }

    public async Task<ControlResult<IReadOnlyList<ProvisioningOperationResponse>>> ListProvisioningAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (await repository.FindProjectAsync(projectId, cancellationToken) is null)
        {
            return ControlResult<IReadOnlyList<ProvisioningOperationResponse>>.Fail(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found.");
        }

        var operations = await repository.ListProvisioningOperationsAsync(projectId, cancellationToken);

        return ControlResult<IReadOnlyList<ProvisioningOperationResponse>>.Ok(
            operations.Select(operation => new ProvisioningOperationResponse(
                operation.Id,
                operation.ProjectId,
                operation.Step,
                operation.Status.ToString(),
                operation.Attempt,
                AuditRedactor.Redact(operation.Error),
                operation.CorrelationId,
                operation.StartedAt,
                operation.CompletedAt)).ToArray());
    }

    private static AuditEntryResponse ToResponse(Domain.Auditing.AuditEntry entry) => new(
        entry.Id,
        entry.OccurredAt,
        entry.Actor,
        entry.Action,
        entry.TargetType,
        entry.TargetId,
        entry.ProjectId,
        AuditRedactor.Redact(entry.Details),
        entry.CorrelationId);
}
