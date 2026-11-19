using DomiBase.Application.Schema;

namespace DomiBase.UnitTests;

/// <summary>
/// A migration must be immutable and fully validated before any privileged connection opens.
/// </summary>
public class MigrationPlanTests
{
    [Fact]
    public void Checksum_IsStableAcrossInstancesAndProcesses()
    {
        var first = Plan([CreateTable("contactos"), Policy("contactos_select")]);
        var second = Plan([CreateTable("contactos"), Policy("contactos_select")]);

        Assert.Equal(first.Checksum, second.Checksum);
        Assert.StartsWith(MigrationChecksum.VersionPrefix, first.Checksum);
        Assert.Equal(64, first.Checksum.Length - MigrationChecksum.VersionPrefix.Length);
    }

    [Fact]
    public void Checksum_ChangesWhenAnyOperationChanges()
    {
        var original = Plan([CreateTable("contactos")]);
        var changed = Plan([CreateTable("contactos", "text")]);
        var reordered = Plan([Policy("contactos_select"), CreateTable("contactos")]);

        Assert.NotEqual(original.Checksum, changed.Checksum);
        Assert.NotEqual(original.Checksum, reordered.Checksum);
    }

    [Fact]
    public void TryBuildPlan_AcceptsValidDefinition()
    {
        Assert.True(MigrationService.TryBuildPlan(
            1, "initial_schema", [CreateTable("contactos")], out var plan, out var error));
        Assert.NotNull(plan);
        Assert.Null(error);
        Assert.Equal(1, plan!.Version);
        Assert.Equal("initial_schema", plan.Name);
        Assert.Equal(MigrationChecksum.Compute(plan.Operations), plan.Checksum);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MigrationService.MaxVersion + 1)]
    public void TryBuildPlan_RejectsInvalidVersion(int version)
    {
        Assert.False(MigrationService.TryBuildPlan(
            version, "initial_schema", [CreateTable("contactos")], out _, out var error));
        Assert.Contains("version", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("InitialSchema")]
    [InlineData("bad name")]
    [InlineData("tabla; DROP TABLE usuarios")]
    public void TryBuildPlan_RejectsUnsafeName(string name)
    {
        Assert.False(MigrationService.TryBuildPlan(
            1, name, [CreateTable("contactos")], out _, out var error));
        Assert.Contains("identifier", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildPlan_RejectsEmptyOrOversizedOperationLists()
    {
        Assert.False(MigrationService.TryBuildPlan(1, "migration", [], out _, out _));
        Assert.False(MigrationService.TryBuildPlan(
            1, "migration", Enumerable.Repeat(CreateTable("contactos"), MigrationService.MaxOperations + 1).ToList(),
            out _, out _));
    }

    [Fact]
    public void TryBuildPlan_RejectsOperationFailingDdlValidator()
    {
        Assert.False(MigrationService.TryBuildPlan(
            1, "migration", [CreateTable("contactos; DROP TABLE usuarios")], out _, out var error));
        Assert.Contains("Table name", error);
    }

    private static MigrationPlan Plan(IReadOnlyList<SchemaMutation> operations) =>
        new(1, "plan", operations, MigrationChecksum.Compute(operations));

    private static CreateTableMutation CreateTable(string table, string type = "uuid") =>
        new(table, [new ColumnDefinition("id", type, true, true, false, null, null)]);

    private static CreatePolicyMutation Policy(string name) =>
        new(
            "contactos",
            name,
            [PolicyCommand.Select],
            [SchemaFunctionalRole.Anon],
            "true",
            "true");
}
