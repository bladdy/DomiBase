using DomiBase.Application.Runtime;

namespace DomiBase.UnitTests;

public class RuntimeQueryBuilderTests
{
    [Fact]
    public void BuildSelect_DefaultSelection_UsesOnlyReadableColumns()
    {
        var table = CreateTable();

        var sql = RuntimeQueryBuilder.BuildSelect(
            table,
            new RuntimeReadOptions(null, [], null, null, null));

        Assert.Equal(
            "SELECT \"id\", \"nombre\", \"edad\", \"activo\", \"estado\", \"notas\", \"deleted_at\", \"binario\" " +
            "FROM \"public\".\"contactos\" LIMIT 100 OFFSET 0",
            sql.CommandText);
        Assert.Empty(sql.Parameters);
        Assert.Equal(
            ["id", "nombre", "edad", "activo", "estado", "notas", "deleted_at", "binario"],
            sql.ResultColumns);
    }

    [Fact]
    public void BuildSelect_WithFiltersOrderAndLimits_ParameterizesEveryValue()
    {
        var table = CreateTable();
        var injected = "' OR 1=1; DROP TABLE contactos; --";

        var sql = RuntimeQueryBuilder.BuildSelect(
            table,
            new RuntimeReadOptions(
                ["id", "nombre"],
                [$"estado.eq.{injected}", "deleted_at.is.null", "edad.gte.18", "id.in.1,2"],
                "nombre.desc,id.asc",
                20,
                5));

        Assert.Contains("\"estado\" = @p0::text", sql.CommandText);
        Assert.Contains("\"deleted_at\" IS NULL", sql.CommandText);
        Assert.Contains("\"edad\" >= @p1::int4", sql.CommandText);
        Assert.Contains("\"id\" IN (@p2::uuid, @p3::uuid)", sql.CommandText);
        Assert.Contains("ORDER BY \"nombre\" DESC, \"id\" ASC", sql.CommandText);
        Assert.Contains("LIMIT 20 OFFSET 5", sql.CommandText);
        Assert.DoesNotContain(injected, sql.CommandText);
        Assert.Equal(injected, sql.Parameters[0].Value);
        Assert.Equal("18", sql.Parameters[1].Value);
    }

    [Fact]
    public void BuildSelect_RejectsInjectionInColumnNames()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(["id\"; DROP TABLE contactos; --"], [], null, null, null)));

        Assert.Equal("INVALID_COLUMN", exception.Code);
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public void BuildSelect_RejectsFilterOnNonReadableColumn()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, ["secreto.eq.x"], null, null, null)));

        Assert.Equal("INVALID_COLUMN", exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(-1)]
    public void BuildSelect_RejectsOutOfRangeLimit(int limit)
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, [], null, limit, null)));

        Assert.Equal("INVALID_LIMIT", exception.Code);
    }

    [Fact]
    public void BuildSelect_RejectsUnknownOperator()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, ["estado.raw.1"], null, null, null)));

        Assert.Equal("INVALID_FILTER_OPERATOR", exception.Code);
    }

    [Fact]
    public void BuildSelect_RejectsMoreThanTenFilters()
    {
        var table = CreateTable();
        var filters = Enumerable.Range(0, 11).Select(_ => "estado.eq.nuevo").ToList();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, filters, null, null, null)));

        Assert.Equal("INVALID_FILTER", exception.Code);
    }

    [Fact]
    public void BuildSelect_ReturnsNotFoundWhenSelectIsNotGranted()
    {
        var table = CreateTable() with { CanSelect = false };

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, [], null, null, null)));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("TABLE_NOT_FOUND", exception.Code);
    }

    [Fact]
    public void BuildInsert_ParameterizesValuesAndReturnsReadableColumns()
    {
        var table = CreateTable();
        var values = new Dictionary<string, string?>
        {
            ["nombre"] = "Ana'; DROP TABLE contactos; --",
            ["edad"] = "34",
            ["activo"] = "true",
            ["notas"] = null
        };

        var sql = RuntimeQueryBuilder.BuildInsert(table, values, ["id", "nombre"]);

        Assert.Equal(
            "INSERT INTO \"public\".\"contactos\" (\"nombre\", \"edad\", \"activo\", \"notas\") " +
            "VALUES (@p0::text, @p1::int4, @p2::bool, NULL) RETURNING \"id\", \"nombre\"",
            sql.CommandText);
        Assert.Equal("Ana'; DROP TABLE contactos; --", sql.Parameters[0].Value);
        Assert.Equal(["id", "nombre"], sql.ResultColumns);
    }

    [Fact]
    public void BuildInsert_UsesColumnLevelInsertPrivileges()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildInsert(table, new Dictionary<string, string?> { ["secreto"] = "x" }, null));

        Assert.Equal("INVALID_COLUMN", exception.Code);
    }

    [Fact]
    public void BuildInsert_WithoutSelectPrivilege_DoesNotUseReturning()
    {
        var table = CreateTable() with { CanSelect = false };

        var sql = RuntimeQueryBuilder.BuildInsert(
            table,
            new Dictionary<string, string?> { ["nombre"] = "Ana" },
            null);

        Assert.DoesNotContain("RETURNING", sql.CommandText);
        Assert.Empty(sql.ResultColumns);
    }

    [Fact]
    public void BuildUpdate_RequiresFilterAndParameterizesAssignments()
    {
        var table = CreateTable();

        var sql = RuntimeQueryBuilder.BuildUpdate(
            table,
            new Dictionary<string, string?> { ["estado"] = "pagado" },
            new RuntimeWriteOptions(["id"], ["id.eq.11111111-1111-1111-1111-111111111111"]));

        Assert.Equal(
            "UPDATE \"public\".\"contactos\" SET \"estado\" = @p0::text " +
            "WHERE \"id\" = @p1::uuid RETURNING \"id\"",
            sql.CommandText);
        Assert.Equal("pagado", sql.Parameters[0].Value);

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildUpdate(
                table,
                new Dictionary<string, string?> { ["estado"] = "pagado" },
                new RuntimeWriteOptions(null, [])));
        Assert.Equal("FILTER_REQUIRED", exception.Code);
    }

    [Fact]
    public void BuildUpdate_UsesColumnLevelUpdatePrivileges()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildUpdate(
                table,
                new Dictionary<string, string?> { ["id"] = "11111111-1111-1111-1111-111111111111" },
                new RuntimeWriteOptions(null, ["estado.eq.nuevo"])));

        Assert.Equal("INVALID_COLUMN", exception.Code);
    }

    [Fact]
    public void BuildDelete_RequiresFilter()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildDelete(table, new RuntimeWriteOptions(null, [])));

        Assert.Equal("FILTER_REQUIRED", exception.Code);

        var sql = RuntimeQueryBuilder.BuildDelete(
            table,
            new RuntimeWriteOptions(null, ["estado.eq.antiguo"]));

        Assert.Equal(
            "DELETE FROM \"public\".\"contactos\" WHERE \"estado\" = @p0::text RETURNING " +
            "\"id\", \"nombre\", \"edad\", \"activo\", \"estado\", \"notas\", \"deleted_at\", \"binario\"",
            sql.CommandText);
    }

    [Fact]
    public void BuildDelete_ReturnsNotFoundWhenDeleteIsNotGranted()
    {
        var table = CreateTable() with { CanDelete = false };

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildDelete(table, new RuntimeWriteOptions(null, ["estado.eq.x"])));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("TABLE_NOT_FOUND", exception.Code);
    }

    [Fact]
    public void BuildSelect_RejectsUnsupportedColumnTypeForFilters()
    {
        var table = CreateTable();

        var exception = Assert.Throws<RuntimeQueryException>(() =>
            RuntimeQueryBuilder.BuildSelect(
                table,
                new RuntimeReadOptions(null, ["binario.eq.abc"], null, null, null)));

        Assert.Equal("UNSUPPORTED_COLUMN_TYPE", exception.Code);
    }

    private static RuntimeTableMetadata CreateTable() => new(
        Name: "contactos",
        CanSelect: true,
        CanInsert: true,
        CanUpdate: true,
        CanDelete: true,
        Columns:
        [
            new RuntimeColumn("id", "uuid", CanSelect: true, CanInsert: false, CanUpdate: false),
            new RuntimeColumn("nombre", "text", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("edad", "int4", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("activo", "bool", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("estado", "text", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("notas", "text", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("deleted_at", "timestamptz", CanSelect: true, CanInsert: true, CanUpdate: true),
            new RuntimeColumn("secreto", "text", CanSelect: false, CanInsert: false, CanUpdate: false),
            new RuntimeColumn("binario", "bytea", CanSelect: true, CanInsert: true, CanUpdate: true)
        ]);
}
