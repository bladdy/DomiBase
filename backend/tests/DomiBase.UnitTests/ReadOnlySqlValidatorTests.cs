using DomiBase.Application.Query;

namespace DomiBase.UnitTests;

/// <summary>
/// The editor accepts exactly one read-only statement and cannot be fooled by write keywords
/// hidden in literals, comments or dollar-quoted text.
/// </summary>
public class ReadOnlySqlValidatorTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select id, nombre from contactos where estado = 'nuevo' order by id limit 5")]
    [InlineData("WITH recientes AS (SELECT id FROM contactos) SELECT * FROM recientes")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT 'DROP TABLE contactos' AS texto")]
    [InlineData("SELECT \"update\" FROM contactos")]
    [InlineData("SELECT $$UPDATE contactos SET id = 1$$ AS texto")]
    [InlineData("SELECT $tag$DELETE FROM contactos$tag$ AS texto")]
    [InlineData("SELECT 1 -- INSERT INTO contactos VALUES (1)")]
    [InlineData("/* UPDATE contactos */ SELECT 1")]
    [InlineData("SELECT pg_sleep(0)")]
    public void TryValidate_AcceptsReadOnlyStatements(string sql)
    {
        Assert.True(ReadOnlySqlValidator.TryValidate(sql, out var statement, out var error), error);
        Assert.NotEmpty(statement);
        Assert.DoesNotContain(";", statement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO contactos VALUES (1)")]
    [InlineData("UPDATE contactos SET nombre = 'x'")]
    [InlineData("DELETE FROM contactos")]
    [InlineData("DROP TABLE contactos")]
    [InlineData("CREATE TABLE otro (id int4)")]
    [InlineData("TRUNCATE contactos")]
    [InlineData("GRANT SELECT ON contactos TO anon")]
    [InlineData("SELECT 1 INTO nueva_tabla")]
    [InlineData("SELECT nextval('secuencia')")]
    [InlineData("SELECT set_config('search_path', 'pg_catalog', false)")]
    [InlineData("SELECT pg_read_file('/etc/passwd')")]
    [InlineData("WITH borrar AS (DELETE FROM contactos RETURNING id) SELECT * FROM borrar")]
    public void TryValidate_RejectsWritesAndDangerousFunctions(string sql)
    {
        Assert.False(ReadOnlySqlValidator.TryValidate(sql, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE contactos")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1;;")]
    public void TryValidate_RejectsMultipleStatements(string sql)
    {
        Assert.False(ReadOnlySqlValidator.TryValidate(sql, out _, out var error));
        Assert.Contains("one SQL statement", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT /* inconcluso")]
    [InlineData("SELECT $$inconcluso")]
    public void TryValidate_RejectsEmptyOrUnterminatedInput(string sql)
    {
        Assert.False(ReadOnlySqlValidator.TryValidate(sql, out _, out _));
    }

    [Fact]
    public void TryValidate_RejectsOversizedStatement()
    {
        var sql = "SELECT '" + new string('a', ReadOnlySqlValidator.MaxSqlLength) + "'";
        Assert.False(ReadOnlySqlValidator.TryValidate(sql, out _, out var error));
        Assert.Contains("characters", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_StripsTrailingSemicolonButKeepsLiterals()
    {
        Assert.True(ReadOnlySqlValidator.TryValidate(
            "SELECT 'a;b' AS valor; -- comentario", out var statement, out _));
        Assert.Equal("SELECT 'a;b' AS valor", statement);
    }
}
