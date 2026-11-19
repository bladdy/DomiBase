using DomiBase.Domain.Projects;

namespace DomiBase.Application.Projects;

/// <summary>PostgreSQL cluster resources that belong to exactly one DomiBase project.</summary>
public sealed record ProjectResourceNames(
    string DatabaseName,
    string OwnerRole,
    string RuntimeRole,
    string AnonymousRole,
    string AuthenticatedRole);

/// <summary>
/// Derives and validates resource names before any privileged SQL is built. A project with
/// inconsistent metadata must never be able to drop or alter resources of another project.
/// </summary>
public static class ProjectResourceNaming
{
    public static bool TryResolve(Project project, out ProjectResourceNames names, out string? error)
    {
        names = null!;
        error = null;

        if (project is null)
        {
            error = "Project metadata is missing.";
            return false;
        }

        if (!SlugValidator.IsValid(project.Slug))
        {
            error = $"Project slug '{project.Slug}' is not valid for resource naming.";
            return false;
        }

        var databaseName = ProjectNaming.ToDatabaseName(project.Slug);
        if (!string.Equals(databaseName, project.DatabaseName, StringComparison.Ordinal))
        {
            error = "Project database name does not match its slug. Refusing to touch PostgreSQL resources.";
            return false;
        }

        names = new ProjectResourceNames(
            databaseName,
            ProjectNaming.ToOwnerRole(project.Slug),
            ProjectNaming.ToRuntimeRole(project.Slug),
            ProjectNaming.ToAnonymousRole(project.Slug),
            ProjectNaming.ToAuthenticatedRole(project.Slug));

        // PostgreSQL truncates identifiers past 63 bytes; never generate truncated names.
        if (names.AuthenticatedRole.Length > ProjectNaming.MaxDatabaseNameLength)
        {
            error = "Derived PostgreSQL role names exceed the identifier limit.";
            return false;
        }

        return true;
    }
}
