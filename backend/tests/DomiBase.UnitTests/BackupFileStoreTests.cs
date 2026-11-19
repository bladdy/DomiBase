using System.Security.Cryptography;
using System.Text;
using DomiBase.Application.Backups;
using DomiBase.Infrastructure.Backups;

namespace DomiBase.UnitTests;

/// <summary>
/// The filesystem store derives every path from server-generated GUIDs and must reject any
/// other relative path before touching the disk.
/// </summary>
public class BackupFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemBackupStore _store;

    public BackupFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "domibase-filestore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new FileSystemBackupStore(new BackupOptions { RootPath = _root });
    }

    [Fact]
    public async Task WriteInspectCommitReadDelete_RoundTripsTheDump()
    {
        var projectId = Guid.CreateVersion7();
        var backupId = Guid.CreateVersion7();
        var payload = Encoding.UTF8.GetBytes("PGDMP-fake-dump-content");
        var tempPath = _store.CreateTempPath(projectId, backupId);
        var relativePath = $"{projectId:N}/{backupId:N}.dump";

        Assert.StartsWith(_root, tempPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".dump.tmp", tempPath, StringComparison.Ordinal);

        await using (var output = await _store.OpenWriteAsync(tempPath))
        {
            await output.WriteAsync(payload);
        }

        var (size, hash) = await _store.InspectAsync(tempPath);
        Assert.Equal(payload.Length, size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), hash);
        Assert.False(File.Exists(Path.Combine(_root, relativePath)));

        await _store.CommitAsync(tempPath, relativePath);
        Assert.False(File.Exists(tempPath));

        await using (var input = await _store.OpenReadAsync(relativePath))
        using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }

        await _store.DeleteAsync(relativePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.OpenReadAsync(relativePath));
    }

    [Theory]
    [InlineData("../outside.dump")]
    [InlineData("../../secret.dump")]
    [InlineData("00000000000000000000000000000000/../../secret.dump")]
    [InlineData("/etc/passwd")]
    public async Task FinalPath_RejectsAnythingOutsideTheDerivedPattern(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.InspectAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.CommitAsync(_store.CreateTempPath(Guid.CreateVersion7(), Guid.CreateVersion7()), path));
    }

    [Fact]
    public async Task TempPath_RejectsPathsOutsideTheBackupRoot()
    {
        var foreign = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") + ".dump.tmp");

        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenWriteAsync(foreign));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
