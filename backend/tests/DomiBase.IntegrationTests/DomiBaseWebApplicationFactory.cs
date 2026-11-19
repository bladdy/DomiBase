using DomiBase.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Boots the real API host against the local PostgreSQL container using the
/// development settings from the repository <c>.env</c>. Phase 3 integration
/// tests exercise real provisioning, so PostgreSQL must be running.
/// </summary>
public class DomiBaseWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminToken = "integration-test-admin-token-0123456789";
    public const string AdminUsername = "integration-owner";
    public const string AdminPassword = "integration-owner-password-0123456789";
    public const string AdminSessionSecret = "integration-session-secret-0123456789abcdef";

    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public DomiBaseWebApplicationFactory() : this(null)
    {
    }

    /// <summary>
    /// Applies environment overrides after reading <c>.env</c>. Derivatives use this to
    /// simulate rotated deployment secrets without depending on configuration source order.
    /// </summary>
    protected DomiBaseWebApplicationFactory(IReadOnlyDictionary<string, string?>? overrides)
    {
        var dotEnvPath = DotEnv.Find(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "No local .env file was found. Phase 3 integration tests require PostgreSQL and the DOMIBASE_* development settings.");

        var values = DotEnv.ParseFile(dotEnvPath);

        ControlConnectionString = Require(values, "CONTROL_DB_CONNECTION_STRING");
        ProvisioningConnectionString = Require(values, "PROVISIONING_DB_CONNECTION_STRING");
        ApiKeyPepper = Require(values, "DOMIBASE_API_KEY_PEPPER");
        SecretsKey = Require(values, "DOMIBASE_SECRETS_KEY");

        SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        SetEnvironmentVariable("DOMIBASE_ADMIN_TOKEN", AdminToken);
        SetEnvironmentVariable("DOMIBASE_ADMIN_USERNAME", AdminUsername);
        SetEnvironmentVariable("DOMIBASE_ADMIN_PASSWORD", AdminPassword);
        SetEnvironmentVariable("DOMIBASE_ADMIN_SESSION_SECRET", AdminSessionSecret);
        SetEnvironmentVariable("CONTROL_DB_CONNECTION_STRING", ControlConnectionString);
        SetEnvironmentVariable("PROVISIONING_DB_CONNECTION_STRING", ProvisioningConnectionString);
        SetEnvironmentVariable("DOMIBASE_API_KEY_PEPPER", ApiKeyPepper);
        SetEnvironmentVariable("DOMIBASE_SECRETS_KEY", SecretsKey);
        SetEnvironmentVariable("RUNTIME_DB_HOST", "127.0.0.1");
        SetEnvironmentVariable("RUNTIME_DB_PORT", ExtractPort(ProvisioningConnectionString));
        SetEnvironmentVariable("DOMIBASE_FUNCTIONS_WORKER", FindFunctionWorker());

        BackupRoot = Path.Combine(
            Path.GetTempPath(),
            "domibase-backup-tests",
            Guid.NewGuid().ToString("N"));
        SetEnvironmentVariable("BACKUP_ROOT", BackupRoot);
        SetEnvironmentVariable("BACKUP_TOOL_MODE", "docker");
        SetEnvironmentVariable("BACKUP_DOCKER_CONTAINER", "domibase-postgres");

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                SetEnvironmentVariable(key, value);
            }
        }
    }

    public string ControlConnectionString { get; }

    public string ProvisioningConnectionString { get; }

    /// <summary>Values are exposed only so tests can simulate a rotated keyring; never logged.</summary>
    public string ApiKeyPepper { get; }

    public string SecretsKey { get; }

    /// <summary>Isolated temporary dump directory for this host; removed on disposal.</summary>
    public string BackupRoot { get; }

    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-DomiBase-Admin-Token", AdminToken);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (key, previous) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, previous);
            }

            try
            {
                if (Directory.Exists(BackupRoot))
                {
                    Directory.Delete(BackupRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Temporary files are best-effort only.
            }
        }

        base.Dispose(disposing);
    }

    private void SetEnvironmentVariable(string key, string? value)
    {
        _previousEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is missing from the local .env file.");
        }

        return value;
    }

    private static string ExtractPort(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        return (builder.Port > 0 ? builder.Port : 5432).ToString();
    }

    private static string FindFunctionWorker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "DomiBase.Functions.Worker",
                "bin",
                "Debug",
                "net10.0",
                "DomiBase.Functions.Worker.dll");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "DomiBase.Functions.Worker.dll was not found. Build the solution before running function integration tests.");
    }
}
