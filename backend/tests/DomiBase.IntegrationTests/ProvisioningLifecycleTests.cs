using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Domain.Projects;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DomiBase.IntegrationTests;

public class ProvisioningLifecycleTests(DomiBaseWebApplicationFactory factory)
    : IClassFixture<DomiBaseWebApplicationFactory>
{
    [Fact]
    public async Task RealProvisioning_PersistsEveryStepWithAttemptAndCorrelation()
    {
        var slug = UniqueSlug();
        Guid? projectId = null;
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var client = factory.CreateAdminClient();
            var created = await client.PostAsJsonAsync(
                "/api/v1/projects",
                new { slug, name = "Provisioning steps test" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            projectId = json.RootElement.GetProperty("id").GetGuid();

            using var scope = factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var operations = await repository.ListProvisioningOperationsAsync(projectId.Value);

            var expectedSteps = new[]
            {
                ProvisioningSteps.Roles,
                ProvisioningSteps.Database,
                ProvisioningSteps.DatabasePermissions,
                ProvisioningSteps.AuthSchema
            };

            foreach (var step in expectedSteps)
            {
                var operation = Assert.Single(operations, item => item.Step == step);
                Assert.Equal(ProvisioningStatus.Succeeded, operation.Status);
                Assert.Equal(1, operation.Attempt);
                Assert.False(string.IsNullOrWhiteSpace(operation.CorrelationId));
                Assert.True(operation.StartedAt <= operation.CompletedAt);
            }

            Assert.All(operations, operation =>
                Assert.Equal(operations[0].CorrelationId, operation.CorrelationId));
        }
        finally
        {
            await cleanup.DropProjectAsync(slug, projectId);
        }
    }

    [Fact]
    public async Task Provisioner_ReportsRunningThenTerminalStateForEveryStep()
    {
        var slug = UniqueSlug();
        var project = NewProject(slug);
        var sink = new RecordingSink();

        try
        {
            var provisioner = factory.Services.GetRequiredService<IProjectProvisioner>();
            var credentials = new ProjectRuntimeCredentials(
                ProjectNaming.ToRuntimeRole(slug),
                "unit-test-runtime-password-0123456789");

            var outcome = await provisioner.ProvisionAsync(project, credentials, CancellationToken.None, sink);

            Assert.True(outcome.Succeeded, outcome.Error);
            foreach (var step in new[]
                     {
                         ProvisioningSteps.Roles,
                         ProvisioningSteps.Database,
                         ProvisioningSteps.DatabasePermissions,
                         ProvisioningSteps.AuthSchema
                     })
            {
                var events = sink.Events.Where(item => item.Step == step).ToList();
                Assert.Equal(2, events.Count);
                Assert.Equal(ProvisioningStatus.Running, events[0].Status);
                Assert.Equal(ProvisioningStatus.Succeeded, events[1].Status);
            }
        }
        finally
        {
            await new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString)
                .DropProjectAsync(slug, null);
        }
    }

    [Fact]
    public async Task DeprovisionAsync_RemovesOnlyTheRequestedProjectAndIsIdempotent()
    {
        const string firstSlug = "it-deprovision-first";
        const string secondSlug = "it-deprovision-second";
        var first = NewProject(firstSlug);
        var second = NewProject(secondSlug);
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);

        try
        {
            using var client = factory.CreateAdminClient();
            await CreateProjectAsync(client, firstSlug);
            await CreateProjectAsync(client, secondSlug);

            var provisioner = factory.Services.GetRequiredService<IProjectProvisioner>();
            var outcome = await provisioner.DeprovisionAsync(first);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Contains(outcome.Steps, step => step.Step == ProvisioningSteps.DatabaseCleanup && step.Succeeded);
            Assert.Contains(outcome.Steps, step => step.Step == ProvisioningSteps.RoleCleanup && step.Succeeded);

            var firstArtifacts = await ReadArtifactsAsync(firstSlug);
            Assert.False(firstArtifacts.Database);
            Assert.False(firstArtifacts.Owner);
            Assert.False(firstArtifacts.Runtime);
            Assert.False(firstArtifacts.Anonymous);
            Assert.False(firstArtifacts.Authenticated);

            var secondArtifacts = await ReadArtifactsAsync(secondSlug);
            Assert.True(secondArtifacts.Database);
            Assert.True(secondArtifacts.Owner);
            Assert.True(secondArtifacts.Runtime);
            Assert.True(secondArtifacts.Anonymous);
            Assert.True(secondArtifacts.Authenticated);

            var repeated = await provisioner.DeprovisionAsync(first);
            Assert.True(repeated.Succeeded, repeated.Error);
            Assert.True((await ReadArtifactsAsync(secondSlug)).Database);
        }
        finally
        {
            await cleanup.DropProjectAsync(firstSlug, await FindProjectIdAsync(firstSlug));
            await cleanup.DropProjectAsync(secondSlug, await FindProjectIdAsync(secondSlug));
        }
    }

    [Fact]
    public async Task CompensateAsync_RemovesPartiallyCreatedRoles()
    {
        var slug = UniqueSlug();
        var project = NewProject(slug);
        var owner = ProjectNaming.ToOwnerRole(slug);
        var anonymous = ProjectNaming.ToAnonymousRole(slug);

        try
        {
            await ExecuteAdminAsync($"CREATE ROLE {Quote(owner)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await ExecuteAdminAsync($"CREATE ROLE {Quote(anonymous)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");

            var provisioner = factory.Services.GetRequiredService<IProjectProvisioner>();
            var outcome = await provisioner.CompensateAsync(project);

            Assert.True(outcome.Succeeded, outcome.Error);
            var artifacts = await ReadArtifactsAsync(slug);
            Assert.False(artifacts.Owner);
            Assert.False(artifacts.Anonymous);
        }
        finally
        {
            await new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString)
                .DropProjectAsync(slug, null);
        }
    }

    [Fact]
    public async Task CompensateAsync_RefusesToDropDatabaseOwnedByAnotherRole()
    {
        var slug = UniqueSlug();
        var project = NewProject(slug);
        var database = ProjectNaming.ToDatabaseName(slug);
        // The runtime role exists in a real project, but it must never be accepted as the
        // owner of its database: cleanup only removes databases owned by the owner role.
        var foreignOwner = ProjectNaming.ToRuntimeRole(slug);

        try
        {
            await ExecuteAdminAsync($"CREATE ROLE {Quote(foreignOwner)} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
            await ExecuteAdminAsync($"CREATE DATABASE {Quote(database)} OWNER {Quote(foreignOwner)} TEMPLATE template0");

            var provisioner = factory.Services.GetRequiredService<IProjectProvisioner>();
            var outcome = await provisioner.CompensateAsync(project);

            Assert.False(outcome.Succeeded);
            Assert.Contains("not by the project owner role", outcome.Error);
            Assert.True((await ReadArtifactsAsync(slug)).Database);
        }
        finally
        {
            await new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString)
                .DropProjectAsync(slug, null);
        }
    }

    private static Project NewProject(string slug) => new()
    {
        Id = Guid.CreateVersion7(),
        Slug = slug,
        Name = "Integration Test",
        DatabaseName = ProjectNaming.ToDatabaseName(slug),
        Status = ProjectStatus.Ready
    };

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = "Integration Test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid?> FindProjectIdAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM projects WHERE slug = @slug", connection);
        command.Parameters.AddWithValue("slug", slug);
        return await command.ExecuteScalarAsync() as Guid?;
    }

    private async Task<(bool Database, bool Owner, bool Runtime, bool Anonymous, bool Authenticated)>
        ReadArtifactsAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(factory.ProvisioningConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @database),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @owner),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @runtime),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @anon),
                   EXISTS(SELECT 1 FROM pg_roles WHERE rolname = @authenticated)
            """,
            connection);
        command.Parameters.AddWithValue("database", ProjectNaming.ToDatabaseName(slug));
        command.Parameters.AddWithValue("owner", ProjectNaming.ToOwnerRole(slug));
        command.Parameters.AddWithValue("runtime", ProjectNaming.ToRuntimeRole(slug));
        command.Parameters.AddWithValue("anon", ProjectNaming.ToAnonymousRole(slug));
        command.Parameters.AddWithValue("authenticated", ProjectNaming.ToAuthenticatedRole(slug));

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4));
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(factory.ProvisioningConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string UniqueSlug() => "p0-" + Guid.NewGuid().ToString("N")[..10];

    private sealed class RecordingSink : IProvisioningStepSink
    {
        private readonly Dictionary<Guid, string> _steps = new();

        public List<(string Step, ProvisioningStatus Status, string? Error)> Events { get; } = new();

        public Task<Guid> BeginAsync(string step, CancellationToken cancellationToken = default)
        {
            var id = Guid.CreateVersion7();
            _steps[id] = step;
            Events.Add((step, ProvisioningStatus.Running, null));
            return Task.FromResult(id);
        }

        public Task CompleteAsync(
            Guid operationId,
            ProvisioningStatus status,
            string? error,
            CancellationToken cancellationToken = default)
        {
            Events.Add((_steps[operationId], status, error));
            return Task.CompletedTask;
        }
    }
}
