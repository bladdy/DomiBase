using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.IntegrationTests;

/// <summary>
/// End-to-end backup coverage against the real PostgreSQL container. Dumps are produced by
/// pg_dump 18 inside the container, restored with pg_restore, and verified by reading the
/// project database directly.
/// </summary>
public class ProjectBackupTests
{
    [Fact]
    public async Task BackupMutateRestore_RestoresTheOriginalRows()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(factory, admin, "bkp-restore");

        try
        {
            await SeedAsync(factory, project.Slug);

            var backup = await CreateBackupAsync(admin, project.Id);
            Assert.Equal("custom", backup.GetProperty("format").GetString());
            Assert.True(backup.GetProperty("sizeBytes").GetInt64() > 0);
            Assert.Equal(64, backup.GetProperty("sha256").GetString()!.Length);

            await MutateAsync(factory, project.Slug);
            Assert.Equal("mutated", await ReadNoteAsync(factory, project.Slug, 1));

            var restored = await admin.PostAsync(
                $"/api/v1/projects/{project.Id}/backups/{backup.GetProperty("id").GetGuid()}/restore",
                new StringContent(string.Empty));
            Assert.Equal(HttpStatusCode.BadRequest, restored.StatusCode);

            using var confirm = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/projects/{project.Id}/backups/{backup.GetProperty("id").GetGuid()}/restore");
            confirm.Headers.Add("X-DomiBase-Confirm", project.Slug);
            using var restoreResponse = await admin.SendAsync(confirm);
            Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);

            // The test process has its own Npgsql pool; the server just terminated its sockets.
            NpgsqlConnection.ClearAllPools();
            Assert.Equal("original", await ReadNoteAsync(factory, project.Slug, 1));
            Assert.False(await ExistsAsync(factory, project.Slug, 3));
            Assert.Equal("Ready", await ReadStatusAsync(admin, project.Id));
            await AssertAuditAsync(factory, project.Id, "project.backup.restored");
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    [Fact]
    public async Task Backups_AreIsolatedBetweenProjects()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var first = await CreateProjectAsync(factory, admin, "bkp-first");
        var second = await CreateProjectAsync(factory, admin, "bkp-second");

        try
        {
            var firstBackup = await CreateBackupAsync(admin, first.Id);
            var secondBackup = await CreateBackupAsync(admin, second.Id);
            Assert.NotEqual(
                firstBackup.GetProperty("id").GetGuid(),
                secondBackup.GetProperty("id").GetGuid());

            var foreignDownload = await admin.GetAsync(
                $"/api/v1/projects/{second.Id}/backups/{firstBackup.GetProperty("id").GetGuid()}/download");
            Assert.Equal(HttpStatusCode.NotFound, foreignDownload.StatusCode);

            using var foreignRestore = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/projects/{second.Id}/backups/{firstBackup.GetProperty("id").GetGuid()}/restore");
            foreignRestore.Headers.Add("X-DomiBase-Confirm", second.Slug);
            using var foreignRestoreResponse = await admin.SendAsync(foreignRestore);
            Assert.Equal(HttpStatusCode.NotFound, foreignRestoreResponse.StatusCode);

            var firstList = await ListBackupsAsync(admin, first.Id);
            var secondList = await ListBackupsAsync(admin, second.Id);
            Assert.Single(firstList);
            Assert.Single(secondList);
            Assert.Equal(firstBackup.GetProperty("id").GetGuid(), firstList[0].GetProperty("id").GetGuid());
            Assert.Equal(secondBackup.GetProperty("id").GetGuid(), secondList[0].GetProperty("id").GetGuid());
        }
        finally
        {
            await CleanupAsync(factory, first, second);
        }
    }

    [Fact]
    public async Task CorruptedDump_IsRejectedBeforeTheDatabaseIsTouched()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(factory, admin, "bkp-hash");

        try
        {
            await SeedAsync(factory, project.Slug);
            var backup = await CreateBackupAsync(admin, project.Id);
            var backupId = backup.GetProperty("id").GetGuid();
            var file = Path.Combine(factory.BackupRoot, $"{project.Id:N}", $"{backupId:N}.dump");
            await File.WriteAllTextAsync(file, "tampered dump");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/projects/{project.Id}/backups/{backupId}/restore");
            request.Headers.Add("X-DomiBase-Confirm", project.Slug);
            using var response = await admin.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(response));
            Assert.Equal("Ready", await ReadStatusAsync(admin, project.Id));
            Assert.Equal("original", await ReadNoteAsync(factory, project.Slug, 1));
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    [Fact]
    public async Task DownloadAndDelete_StreamTheDumpAndRemoveIt()
    {
        using var factory = new DomiBaseWebApplicationFactory();
        using var admin = factory.CreateAdminClient();
        var project = await CreateProjectAsync(factory, admin, "bkp-files");

        try
        {
            var backup = await CreateBackupAsync(admin, project.Id);
            var backupId = backup.GetProperty("id").GetGuid();

            using var download = await admin.GetAsync(
                $"/api/v1/projects/{project.Id}/backups/{backupId}/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            var bytes = await download.Content.ReadAsByteArrayAsync();
            Assert.Equal(backup.GetProperty("sizeBytes").GetInt64(), bytes.Length);
            Assert.Equal("PGDMP", Encoding.ASCII.GetString(bytes, 0, 5));

            using var deletion = await admin.DeleteAsync($"/api/v1/projects/{project.Id}/backups/{backupId}");
            Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            Assert.Empty(await ListBackupsAsync(admin, project.Id));
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await admin.GetAsync($"/api/v1/projects/{project.Id}/backups/{backupId}/download")).StatusCode);
            await AssertAuditAsync(factory, project.Id, "project.backup.deleted");
        }
        finally
        {
            await CleanupAsync(factory, project);
        }
    }

    private static async Task<JsonElement> CreateBackupAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.PostAsync($"/api/v1/projects/{projectId}/backups", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> ListBackupsAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.GetAsync($"/api/v1/projects/{projectId}/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static async Task<ProjectInfo> CreateProjectAsync(
        DomiBaseWebApplicationFactory factory,
        HttpClient admin,
        string prefix)
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}"[..36];
        var response = await admin.PostAsJsonAsync(
            "/api/v1/projects",
            new { slug, name = "Backup integration test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Ready", json.RootElement.GetProperty("status").GetString());
        return new ProjectInfo(slug, json.RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<string> ReadStatusAsync(HttpClient admin, Guid projectId)
    {
        var response = await admin.GetAsync($"/api/v1/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task SeedAsync(DomiBaseWebApplicationFactory factory, string slug)
    {
        await using var connection = await OpenProjectAsync(factory, slug);
        await ExecuteAsync(connection, "CREATE TABLE backup_items (id integer PRIMARY KEY, note text NOT NULL)");
        await ExecuteAsync(
            connection,
            "INSERT INTO backup_items (id, note) VALUES (1, 'original'), (2, 'original-2')");
    }

    private static async Task MutateAsync(DomiBaseWebApplicationFactory factory, string slug)
    {
        await using var connection = await OpenProjectAsync(factory, slug);
        await ExecuteAsync(connection, "UPDATE backup_items SET note = 'mutated' WHERE id = 1");
        await ExecuteAsync(connection, "INSERT INTO backup_items (id, note) VALUES (3, 'added-after-backup')");
    }

    private static async Task<string?> ReadNoteAsync(DomiBaseWebApplicationFactory factory, string slug, int id)
    {
        await using var connection = await OpenProjectAsync(factory, slug);
        await using var command = new NpgsqlCommand(
            "SELECT note FROM backup_items WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<bool> ExistsAsync(DomiBaseWebApplicationFactory factory, string slug, int id)
    {
        await using var connection = await OpenProjectAsync(factory, slug);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM backup_items WHERE id = @id)",
            connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<NpgsqlConnection> OpenProjectAsync(
        DomiBaseWebApplicationFactory factory,
        string slug)
    {
        var builder = new NpgsqlConnectionStringBuilder(factory.ProvisioningConnectionString)
        {
            Database = ProjectNaming.ToDatabaseName(slug)
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE \"{ProjectNaming.ToOwnerRole(slug)}\"");
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertAuditAsync(
        DomiBaseWebApplicationFactory factory,
        Guid projectId,
        string action)
    {
        await using var connection = new NpgsqlConnection(factory.ControlConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit_entries WHERE action = @action AND target_id = @id",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("id", projectId.ToString());
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 1);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task CleanupAsync(
        DomiBaseWebApplicationFactory factory,
        params ProjectInfo[] projects)
    {
        var cleanup = new ProjectCleanup(factory.ProvisioningConnectionString, factory.ControlConnectionString);
        foreach (var project in projects)
        {
            await cleanup.DropProjectAsync(project.Slug, project.Id);
        }
    }

    private sealed record ProjectInfo(string Slug, Guid Id);
}
