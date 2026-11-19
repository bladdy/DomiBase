using DomiBase.Infrastructure.Configuration;

namespace DomiBase.UnitTests;

public class DotEnvTests
{
    [Fact]
    public void Parse_ReadsKeysValuesQuotesAndSkipsComments()
    {
        var lines = new[]
        {
            "# comment",
            "",
            "PLAIN=value",
            "export EXPORTED=exported-value",
            "QUOTED=\"quoted value\"",
            "SINGLE='single value'",
            "WITH_EQUALS=a=b=c",
            "NO_EQUALS_IGNORED"
        };

        var values = DotEnv.Parse(lines);

        Assert.Equal("value", values["PLAIN"]);
        Assert.Equal("exported-value", values["EXPORTED"]);
        Assert.Equal("quoted value", values["QUOTED"]);
        Assert.Equal("single value", values["SINGLE"]);
        Assert.Equal("a=b=c", values["WITH_EQUALS"]);
        Assert.False(values.ContainsKey("NO_EQUALS_IGNORED"));
    }
}
