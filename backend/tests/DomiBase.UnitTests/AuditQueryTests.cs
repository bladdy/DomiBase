using DomiBase.Application.Auditing;
using DomiBase.Application.Projects;

namespace DomiBase.UnitTests;

public class AuditQueryTests
{
    [Fact]
    public void Parse_WithNoFilters_UsesBoundedDefaults()
    {
        var result = AuditQuery.Parse(new Dictionary<string, string?>());

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(AuditQuery.DefaultLimit, result.Value!.Limit);
        Assert.Equal(0, result.Value.Offset);
        Assert.Null(result.Value.ProjectId);
    }

    [Fact]
    public void Parse_AcceptsValidFilters()
    {
        var values = new Dictionary<string, string?>
        {
            ["projectId"] = "11111111-1111-7111-8111-111111111111",
            ["action"] = " api_key.create ",
            ["actor"] = "admin",
            ["targetType"] = "api_key",
            ["from"] = "2026-11-18T00:00:00Z",
            ["to"] = "2026-11-19T00:00:00Z",
            ["limit"] = "200",
            ["offset"] = "40"
        };

        var result = AuditQuery.Parse(values);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(Guid.Parse("11111111-1111-7111-8111-111111111111"), result.Value!.ProjectId);
        Assert.Equal("api_key.create", result.Value.Action);
        Assert.Equal("admin", result.Value.Actor);
        Assert.Equal("api_key", result.Value.TargetType);
        Assert.NotNull(result.Value.From);
        Assert.NotNull(result.Value.To);
        Assert.Equal(200, result.Value.Limit);
        Assert.Equal(40, result.Value.Offset);
    }

    [Theory]
    [InlineData("unknown", "1")]
    [InlineData("projectId", "not-a-guid")]
    [InlineData("action", "project.create' OR 1=1")]
    [InlineData("from", "yesterday")]
    [InlineData("limit", "0")]
    [InlineData("limit", "201")]
    [InlineData("offset", "-1")]
    public void Parse_RejectsInvalidFilters(string key, string value)
    {
        var result = AuditQuery.Parse(new Dictionary<string, string?> { [key] = value });

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, result.Error!.Code);
    }

    [Fact]
    public void Parse_RejectsInvertedDateRange()
    {
        var result = AuditQuery.Parse(new Dictionary<string, string?>
        {
            ["from"] = "2026-11-19T00:00:00Z",
            ["to"] = "2026-11-18T00:00:00Z"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, result.Error!.Code);
    }
}
