using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DomiBase.IntegrationTests;

[Collection(StorageCollection.Name)]
public sealed class StorageTests
{
    private readonly StorageFixture _fixture;

    public StorageTests(StorageFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateBucket_ThenList_ReturnsBucket()
    {
        using var admin = _fixture.Factory.CreateAdminClient();
        var bucketName = "test-" + Guid.NewGuid().ToString("N")[..6];

        var create = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{_fixture.ProjectId}/buckets",
            new { name = bucketName, publicAccess = true });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var listJson = await admin.GetStringAsync(
            $"/api/v1/projects/{_fixture.ProjectId}/buckets");
        using var list = JsonDocument.Parse(listJson);

        Assert.Contains(list.RootElement.EnumerateArray(), item =>
            item.GetProperty("name").GetString() == bucketName);
    }

    [Fact]
    public async Task DuplicateBucketName_ReturnsConflict()
    {
        var bucketName = "dup-" + Guid.NewGuid().ToString("N")[..6];
        using var admin = _fixture.Factory.CreateAdminClient();

        var first = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{_fixture.ProjectId}/buckets",
            new { name = bucketName, publicAccess = true });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await admin.PostAsJsonAsync(
            $"/api/v1/projects/{_fixture.ProjectId}/buckets",
            new { name = bucketName, publicAccess = false });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Upload_IsForbidden()
    {
        var bucketName = "anon-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: true);

        using var anon = _fixture.CreateRuntimeClient();
        var response = await anon.PostAsync(
            $"/storage/v1/{bucketName}/file.txt",
            new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUpload_ThenAnonymousDownload_OnPublicBucket_ReturnsContent()
    {
        var bucketName = "pub-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: true);
        var token = await _fixture.RegisterUserAsync();
        const string content = "contenido-publico";

        using var auth = _fixture.CreateAuthenticatedClient(token);
        var upload = await auth.PostAsync(
            $"/storage/v1/{bucketName}/notes/hello.txt",
            new StringContent(content, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var anon = _fixture.CreateRuntimeClient();
        var download = await anon.GetAsync($"/storage/v1/{bucketName}/notes/hello.txt");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_CannotReadPrivateBucket_ButAuthenticatedCan()
    {
        var bucketName = "priv-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: false);
        var token = await _fixture.RegisterUserAsync();
        const string content = "secreto";

        using var auth = _fixture.CreateAuthenticatedClient(token);
        var upload = await auth.PostAsync(
            $"/storage/v1/{bucketName}/secret.txt",
            new StringContent(content, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var anon = _fixture.CreateRuntimeClient();
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await anon.GetAsync($"/storage/v1/{bucketName}/secret.txt")).StatusCode);

        var authDownload = await auth.GetAsync($"/storage/v1/{bucketName}/secret.txt");
        Assert.Equal(HttpStatusCode.OK, authDownload.StatusCode);
        Assert.Equal(content, await authDownload.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedUrl_AllowsDownload_FromPrivateBucket()
    {
        var bucketName = "signed-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: false);
        var token = await _fixture.RegisterUserAsync();
        const string content = "por-firma";

        using var auth = _fixture.CreateAuthenticatedClient(token);
        var upload = await auth.PostAsync(
            $"/storage/v1/{bucketName}/file.txt",
            new StringContent(content, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var sign = await auth.PostAsync($"/storage/v1/{bucketName}/signed/file.txt?expiresIn=600", null);
        Assert.Equal(HttpStatusCode.OK, sign.StatusCode);

        using var signJson = JsonDocument.Parse(await sign.Content.ReadAsStringAsync());
        var url = signJson.RootElement.GetProperty("data").GetProperty("url").GetString()!;

        using var anon = _fixture.CreateRuntimeClient();
        var download = await anon.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedUrl_WithTamperedSignature_IsRejected()
    {
        var bucketName = "tamper-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: false);
        var token = await _fixture.RegisterUserAsync();

        using var auth = _fixture.CreateAuthenticatedClient(token);
        await auth.PostAsync(
            $"/storage/v1/{bucketName}/file.txt",
            new StringContent("data", Encoding.UTF8, "text/plain"));
        var sign = await auth.PostAsync($"/storage/v1/{bucketName}/signed/file.txt?expiresIn=600", null);
        using var signJson = JsonDocument.Parse(await sign.Content.ReadAsStringAsync());
        var url = signJson.RootElement.GetProperty("data").GetProperty("url").GetString()!;
        var tampered = url.Replace("signature=", "signature=0");

        using var anon = _fixture.CreateRuntimeClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(tampered)).StatusCode);
    }

    [Fact]
    public async Task SignedUrl_FromOtherProject_CannotReadThisProject()
    {
        // A URL signed for project A must not authorize access to project B even when the
        // bucket and path names match. The project id is part of the signed payload.
        var bucketName = "isolated-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: false);
        var token = await _fixture.RegisterUserAsync();

        using var auth = _fixture.CreateAuthenticatedClient(token);
        await auth.PostAsync(
            $"/storage/v1/{bucketName}/file.txt",
            new StringContent("privado", Encoding.UTF8, "text/plain"));
        var sign = await auth.PostAsync($"/storage/v1/{bucketName}/signed/file.txt?expiresIn=600", null);
        using var signJson = JsonDocument.Parse(await sign.Content.ReadAsStringAsync());
        var url = signJson.RootElement.GetProperty("data").GetProperty("url").GetString()!;

        var (otherApiKey, otherProjectId) = await _fixture.CreateSecondProjectAsync();
        try
        {
            using var admin = _fixture.Factory.CreateAdminClient();
            await admin.PostAsJsonAsync(
                $"/api/v1/projects/{otherProjectId}/buckets",
                new { name = bucketName, publicAccess = false });

            using var otherClient = _fixture.Factory.CreateClient();
            otherClient.DefaultRequestHeaders.Add("apikey", otherApiKey);
            Assert.Equal(HttpStatusCode.Unauthorized, (await otherClient.GetAsync(url)).StatusCode);
        }
        finally
        {
            await _fixture.CleanupSecondProjectAsync(otherProjectId);
        }
    }

    [Fact]
    public async Task ListWithPrefix_ReturnsOnlyMatchingObjects()
    {
        var bucketName = "list-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: true);
        var token = await _fixture.RegisterUserAsync();

        using var auth = _fixture.CreateAuthenticatedClient(token);
        await auth.PostAsync($"/storage/v1/{bucketName}/a/one.txt", new StringContent("1"));
        await auth.PostAsync($"/storage/v1/{bucketName}/b/two.txt", new StringContent("2"));

        using var anon = _fixture.CreateRuntimeClient();
        var listJson = await anon.GetStringAsync($"/storage/v1/{bucketName}?prefix=a/");
        using var list = JsonDocument.Parse(listJson);

        var paths = list.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString())
            .ToList();
        Assert.Contains("a/one.txt", paths);
        Assert.DoesNotContain("b/two.txt", paths);
    }

    [Fact]
    public async Task Delete_RemovesObject()
    {
        var bucketName = "del-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: true);
        var token = await _fixture.RegisterUserAsync();

        using var auth = _fixture.CreateAuthenticatedClient(token);
        await auth.PostAsync($"/storage/v1/{bucketName}/file.txt", new StringContent("data"));

        var delete = await auth.DeleteAsync($"/storage/v1/{bucketName}/file.txt");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var anon = _fixture.CreateRuntimeClient();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anon.GetAsync($"/storage/v1/{bucketName}/file.txt")).StatusCode);
    }

    [Fact]
    public async Task PathTraversal_IsRejected()
    {
        var bucketName = "trav-" + Guid.NewGuid().ToString("N")[..6];
        await _fixture.CreateBucketAsync(bucketName, publicAccess: true);
        var token = await _fixture.RegisterUserAsync();

        using var auth = _fixture.CreateAuthenticatedClient(token);
        var response = await auth.PostAsync(
            $"/storage/v1/{bucketName}/../escape.txt",
            new StringContent("bad"));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }
}
