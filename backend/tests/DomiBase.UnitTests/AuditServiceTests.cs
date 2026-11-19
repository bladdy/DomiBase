using DomiBase.Application.Auditing;
using DomiBase.Application.Projects;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

public class AuditServiceTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    [Fact]
    public async Task List_ReturnsRedactedPageWithPaginationMetadata()
    {
        var repository = new FakeProjectRepository(new Project
        {
            Slug = "audit-unit",
            Name = "Audit unit",
            DatabaseName = ProjectNaming.ToDatabaseName("audit-unit")
        });
        repository.Audits.AddRange(
            new AuditEntry
            {
                Actor = "admin",
                Action = "project.create",
                TargetType = "project",
                TargetId = ProjectId.ToString(),
                ProjectId = ProjectId,
                Details = "{\"apiKey\":\"db_public_supersecretvalue123\"}",
                OccurredAt = DateTimeOffset.Parse("2026-11-18T10:00:00Z")
            },
            new AuditEntry
            {
                Actor = "admin",
                Action = "api_key.create",
                TargetType = "api_key",
                TargetId = Guid.NewGuid().ToString(),
                ProjectId = ProjectId,
                Details = "{\"Prefix\":\"db_public_\"}",
                OccurredAt = DateTimeOffset.Parse("2026-11-18T11:00:00Z")
            });
        var service = new AuditService(repository);
        var query = AuditQuery.Parse(new Dictionary<string, string?>
        {
            ["projectId"] = ProjectId.ToString(),
            ["limit"] = "1"
        }).Value!;

        var result = await service.ListAsync(query);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(1, result.Value.Limit);
        Assert.Single(result.Value.Items);
        Assert.Equal("api_key.create", result.Value.Items[0].Action);
        Assert.DoesNotContain("db_public_supersecretvalue123", result.Value.Items[0].Details ?? string.Empty);
    }

    [Fact]
    public async Task ListProvisioning_WhenProjectDoesNotExist_ReturnsNotFound()
    {
        var repository = new FakeProjectRepository(new Project
        {
            Slug = "audit-unit",
            Name = "Audit unit",
            DatabaseName = ProjectNaming.ToDatabaseName("audit-unit")
        });
        var service = new AuditService(repository);

        var result = await service.ListProvisioningAsync(Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.NotFound, result.Error!.Code);
    }
}
