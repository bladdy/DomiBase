using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Provisioning.Admin;

/// <summary>
/// Executes allowlisted extension DDL inside one project database. The privileged connection
/// switches to the project owner with a transaction-scoped <c>SET LOCAL ROLE</c>; the extension
/// name is validated again here and quoted locally. No SQL, version, schema or role is accepted
/// from clients.
/// </summary>
public sealed class ProjectExtensionAdmin(
    IProvisioningConnectionFactory connectionFactory,
    ILogger<ProjectExtensionAdmin> logger) : IProjectExtensionAdmin
{
    private const string InstallVerb = "CREATE EXTENSION IF NOT EXISTS";
    private const string UninstallVerb = "DROP EXTENSION IF EXISTS";

    public Task InstallAsync(
        ProjectResourceNames names,
        string name,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(names, name, InstallVerb, "install", cancellationToken);

    public Task UninstallAsync(
        ProjectResourceNames names,
        string name,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(names, name, UninstallVerb, "uninstall", cancellationToken);

    public string Describe() => "project-owner-extensions";

    private async Task ExecuteAsync(
        ProjectResourceNames names,
        string name,
        string verb,
        string action,
        CancellationToken cancellationToken)
    {
        if (!PostgresExtensionCatalog.IsAllowed(name))
        {
            throw new ExtensionExecutionException(
                "The PostgreSQL extension is not allowed.",
                ControlErrorCode.Validation,
                "Use a name from the extension catalog.");
        }

        await using var connection = await connectionFactory.OpenAsync(names.DatabaseName, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ProjectSchemaAdmin.ExecuteAsync(
                connection,
                transaction,
                $"SET LOCAL ROLE {Q(names.OwnerRole)}",
                cancellationToken);
            await ProjectSchemaAdmin.ExecuteAsync(
                connection,
                transaction,
                $"{verb} {Q(name)}",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning(
                "Extension {Action} rejected for {Extension} in database {DatabaseName}: {SqlState}",
                action,
                name,
                names.DatabaseName,
                exception.SqlState);
            throw Translate(exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static ExtensionExecutionException Translate(PostgresException exception) => exception.SqlState switch
    {
        "42710" or "42P06" => new ExtensionExecutionException(
            "The extension is already installed in this project."),
        "2BP01" => new ExtensionExecutionException(
            "The extension has dependent objects and cannot be removed.",
            ControlErrorCode.Conflict,
            "Remove objects that depend on the extension first."),
        "42501" => new ExtensionExecutionException(
            "The extension operation is not permitted for this project.",
            ControlErrorCode.Conflict),
        "57014" => new ExtensionExecutionException(
            "The extension operation timed out.",
            ControlErrorCode.Conflict,
            "Retry later."),
        _ => new ExtensionExecutionException(
            "The database rejected the extension operation.",
            ControlErrorCode.Conflict,
            $"PostgreSQL state {exception.SqlState}.")
    };

    private static string Q(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
