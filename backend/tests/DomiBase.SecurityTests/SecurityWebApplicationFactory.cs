using Microsoft.AspNetCore.Mvc.Testing;

namespace DomiBase.SecurityTests;

/// <summary>
/// Boots the real API host with isolated test configuration. No real database is
/// contacted by these tests: they exercise routing and administrative authentication.
/// </summary>
public sealed class SecurityWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminToken = "security-test-admin-token-0123456789";
    public const string AdminUsername = "security-owner";
    public const string AdminPassword = "security-owner-password-0123456789";
    public const string AdminSessionSecret = "security-session-secret-0123456789abcdef";

    private const string TestConnectionString =
        "Host=127.0.0.1;Port=5433;Database=domibase_control;Username=test;Password=test;Timeout=2;Command Timeout=2";

    public SecurityWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("DOMIBASE_ADMIN_TOKEN", AdminToken);
        Environment.SetEnvironmentVariable("DOMIBASE_ADMIN_USERNAME", AdminUsername);
        Environment.SetEnvironmentVariable("DOMIBASE_ADMIN_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("DOMIBASE_ADMIN_SESSION_SECRET", AdminSessionSecret);
        Environment.SetEnvironmentVariable("CONTROL_DB_CONNECTION_STRING", TestConnectionString);
        Environment.SetEnvironmentVariable("PROVISIONING_DB_CONNECTION_STRING", TestConnectionString);
        Environment.SetEnvironmentVariable("DOMIBASE_API_KEY_PEPPER", "security-test-pepper-0123456789");
        Environment.SetEnvironmentVariable(
            "DOMIBASE_SECRETS_KEY",
            "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");
        Environment.SetEnvironmentVariable(
            "BACKUP_ROOT",
            Path.Combine(Path.GetTempPath(), "domibase-backup-security-tests"));
        Environment.SetEnvironmentVariable("BACKUP_TOOL_MODE", "docker");
        Environment.SetEnvironmentVariable("BACKUP_DOCKER_CONTAINER", "domibase-postgres");
    }
}
