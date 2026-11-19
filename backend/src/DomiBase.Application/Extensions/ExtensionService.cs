using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using DomiBase.Domain.Auditing;

namespace DomiBase.Application.Extensions;

/// <summary>
/// Control Plane orchestration for PostgreSQL extensions. It resolves the project from
/// trusted metadata, accepts only names from <see cref="PostgresExtensionCatalog"/>, delegates
/// catalog reads and DDL to the provisioning module, and audits every operation without SQL.
/// </summary>
public sealed class ExtensionService(
    IProjectRepository repository,
    ProjectSchemaResolver resolver,
    IProjectExtensionCatalog catalog,
    IProjectExtensionAdmin admin)
{
    public const string ListAction = "extension.list";
    public const string InstallAction = "extension.install";
    public const string UninstallAction = "extension.uninstall";

    public async Task<ControlResult<IReadOnlyList<PostgresExtension>>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<IReadOnlyList<PostgresExtension>>.Fail(
                resolved.Error.Code, resolved.Error.Message, resolved.Error.Hint);
        }

        IReadOnlyList<PostgresExtension> extensions;
        try
        {
            extensions = await catalog.ListAsync(resolved.Names!.DatabaseName, cancellationToken);
        }
        catch (ExtensionExecutionException exception)
        {
            return ControlResult<IReadOnlyList<PostgresExtension>>.Fail(
                exception.Code, exception.Message, exception.Hint);
        }

        await AuditAsync(
            projectId,
            resolved.Names!.DatabaseName,
            ListAction,
            new { database = resolved.Names.DatabaseName, count = extensions.Count, installed = extensions.Count(item => item.Installed) },
            cancellationToken);

        return ControlResult<IReadOnlyList<PostgresExtension>>.Ok(extensions);
    }

    public Task<ControlResult<ExtensionMutationResult>> InstallAsync(
        Guid projectId,
        string? name,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            projectId,
            name,
            InstallAction,
            (names, extension, token) => admin.InstallAsync(names, extension, token),
            cancellationToken);

    public Task<ControlResult<ExtensionMutationResult>> UninstallAsync(
        Guid projectId,
        string? name,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            projectId,
            name,
            UninstallAction,
            (names, extension, token) => admin.UninstallAsync(names, extension, token),
            cancellationToken);

    private async Task<ControlResult<ExtensionMutationResult>> MutateAsync(
        Guid projectId,
        string? requestedName,
        string auditAction,
        Func<ProjectResourceNames, string, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        // Validation happens before resolution and before any connection is opened.
        if (!PostgresExtensionCatalog.TryGet(requestedName?.Trim(), out var definition))
        {
            return ControlResult<ExtensionMutationResult>.Fail(
                ControlErrorCode.Validation,
                "The PostgreSQL extension is not allowed.",
                "Use a name from the extension catalog.");
        }

        var resolved = await resolver.ResolveAsync(projectId, cancellationToken);
        if (resolved.Error is not null)
        {
            return ControlResult<ExtensionMutationResult>.Fail(
                resolved.Error.Code, resolved.Error.Message, resolved.Error.Hint);
        }

        try
        {
            await operation(resolved.Names!, definition.Name, cancellationToken);
        }
        catch (ExtensionExecutionException exception)
        {
            return ControlResult<ExtensionMutationResult>.Fail(
                exception.Code, exception.Message, exception.Hint);
        }

        var result = new ExtensionMutationResult(definition.Name, auditAction);

        await AuditAsync(
            projectId,
            resolved.Names!.DatabaseName,
            auditAction,
            new { database = resolved.Names.DatabaseName, extension = definition.Name },
            cancellationToken);

        return ControlResult<ExtensionMutationResult>.Ok(result);
    }

    private async Task AuditAsync(
        Guid projectId,
        string database,
        string action,
        object details,
        CancellationToken cancellationToken)
    {
        await repository.AddAuditAsync(
            new AuditEntry
            {
                Actor = ProjectService.AdminActor,
                Action = action,
                TargetType = "project",
                TargetId = projectId.ToString(),
                ProjectId = projectId,
                Details = JsonSerializer.Serialize(details)
            },
            cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
    }
}
