using DomiBase.Domain.Projects;

namespace DomiBase.Application.Projects;

/// <summary>Runtime PostgreSQL login credentials for a project (derived + generated).</summary>
public sealed record ProjectRuntimeCredentials(string Username, string Password);
