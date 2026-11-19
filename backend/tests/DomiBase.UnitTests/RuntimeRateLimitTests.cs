using DomiBase.Application.Runtime;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;

namespace DomiBase.UnitTests;

public class RuntimeRateLimitTests
{
    [Fact]
    public void ProjectRateLimits_AcceptsNullAndBoundaryValues()
    {
        Assert.True(ProjectRateLimits.IsValidPermits(null));
        Assert.True(ProjectRateLimits.IsValidPermits(ProjectRateLimits.MinPermits));
        Assert.True(ProjectRateLimits.IsValidPermits(ProjectRateLimits.MaxPermits));
        Assert.False(ProjectRateLimits.IsValidPermits(0));
        Assert.False(ProjectRateLimits.IsValidPermits(ProjectRateLimits.MaxPermits + 1));

        Assert.True(ProjectRateLimits.IsValidWindowSeconds(null));
        Assert.True(ProjectRateLimits.IsValidWindowSeconds(ProjectRateLimits.MinWindowSeconds));
        Assert.True(ProjectRateLimits.IsValidWindowSeconds(ProjectRateLimits.MaxWindowSeconds));
        Assert.False(ProjectRateLimits.IsValidWindowSeconds(0));
        Assert.False(ProjectRateLimits.IsValidWindowSeconds(ProjectRateLimits.MaxWindowSeconds + 1));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(10_001, 60)]
    [InlineData(10, 0)]
    [InlineData(10, 3_601)]
    public void ProjectRateLimits_RejectsOutOfRangeOverrides(int permits, int windowSeconds)
    {
        Assert.False(
            ProjectRateLimits.TryValidate(permits, windowSeconds, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CreateRuntimeOptions_UsesDocumentedDefaults()
    {
        var options = RuntimeServiceCollectionExtensions.CreateRuntimeOptions(
            new ConfigurationBuilder().Build());

        Assert.Equal(RuntimeOptions.DefaultRateLimitPermits, options.RateLimitPermits);
        Assert.Equal(RuntimeOptions.DefaultRateLimitWindowSeconds, options.RateLimitWindowSeconds);
    }

    [Fact]
    public void CreateRuntimeOptions_UsesConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RUNTIME_RATE_LIMIT_PERMITS"] = "25",
                ["RUNTIME_RATE_LIMIT_WINDOW_SECONDS"] = "120"
            })
            .Build();

        var options = RuntimeServiceCollectionExtensions.CreateRuntimeOptions(configuration);

        Assert.Equal(25, options.RateLimitPermits);
        Assert.Equal(120, options.RateLimitWindowSeconds);
    }

    [Theory]
    [InlineData("RUNTIME_RATE_LIMIT_PERMITS", "0")]
    [InlineData("RUNTIME_RATE_LIMIT_PERMITS", "10001")]
    [InlineData("RUNTIME_RATE_LIMIT_WINDOW_SECONDS", "0")]
    [InlineData("RUNTIME_RATE_LIMIT_WINDOW_SECONDS", "3601")]
    public void CreateRuntimeOptions_RejectsInvalidConfiguration(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => RuntimeServiceCollectionExtensions.CreateRuntimeOptions(configuration));
    }

    [Fact]
    public void Partitions_SeparateProjectsAndEffectiveLimits()
    {
        var id = Guid.NewGuid();
        var first = RuntimeRateLimitPartition.ForProject(id, 2, 60);
        var second = RuntimeRateLimitPartition.ForProject(id, 5, 60);
        var otherProject = RuntimeRateLimitPartition.ForProject(Guid.NewGuid(), 2, 60);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, otherProject);
        Assert.StartsWith("project:", first, StringComparison.Ordinal);
        Assert.Contains(id.ToString("D"), first, StringComparison.Ordinal);
    }

    [Fact]
    public void Partitions_UseAddressForUnresolvedKeysWithoutExposingKeyMaterial()
    {
        var address = RuntimeRateLimitPartition.ForAddress("203.0.113.7");
        var unknown = RuntimeRateLimitPartition.ForAddress(null);

        Assert.Equal("ip:203.0.113.7", address);
        Assert.Equal("ip:unknown", unknown);
        Assert.DoesNotContain("db_public_", address, StringComparison.Ordinal);
    }
}
