using System.Text.Json;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using DomiBase.Provisioning.Admin;

namespace DomiBase.UnitTests;

/// <summary>
/// The validator is the security boundary for administrative DDL: every rejection here
/// prevents privileged SQL from ever being built.
/// </summary>
public class SchemaDdlValidatorTests
{
    [Theory]
    [InlineData("contactos")]
    [InlineData("_interno")]
    [InlineData("tabla_123")]
    [InlineData("a")]
    public void IsIdentifier_AcceptsSafeLowercaseNames(string value) =>
        Assert.True(SchemaDdlValidator.IsIdentifier(value));

    [Theory]
    [InlineData("Contactos")]
    [InlineData("1contactos")]
    [InlineData("con-tactos")]
    [InlineData("contactos; DROP TABLE usuarios")]
    [InlineData("contactos\" OR 1=1 --")]
    [InlineData("pg_catalog")]
    [InlineData("")]
    public void IsIdentifier_RejectsUnsafeNames(string value) =>
        Assert.False(SchemaDdlValidator.IsIdentifier(value));

    [Fact]
    public void IsIdentifier_RejectsNamesLongerThanPostgreSqlLimit() =>
        Assert.False(SchemaDdlValidator.IsIdentifier(new string('a', SchemaDdlValidator.MaxIdentifierLength + 1)));

    [Theory]
    [InlineData("uuid")]
    [InlineData("text")]
    [InlineData("int4")]
    [InlineData("boolean")]
    [InlineData("timestamptz")]
    [InlineData("jsonb")]
    [InlineData("varchar(255)")]
    [InlineData("varchar(8000)")]
    [InlineData("integer")]
    [InlineData("bigint")]
    [InlineData("bytea")]
    [InlineData("inet")]
    [InlineData("interval")]
    public void IsType_AcceptsAllowlistedTypes(string value) =>
        Assert.True(SchemaDdlValidator.IsType(value));

    [Theory]
    [InlineData("text; DROP TABLE usuarios")]
    [InlineData("varchar(99999)")]
    [InlineData("varchar(0)")]
    [InlineData("int4; DROP TABLE usuarios")]
    [InlineData("")]
    public void IsType_RejectsUnknownOrUnsafeTypes(string value) =>
        Assert.False(SchemaDdlValidator.IsType(value));

    [Fact]
    public void TryBuildDefault_EscapesSingleQuotes()
    {
        var value = JsonSerializer.SerializeToElement("O'Brien");

        Assert.True(SchemaDdlValidator.TryBuildDefault(value, out var sql, out var error));
        Assert.Equal("'O''Brien'", sql);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("gen_random_uuid()")]
    [InlineData("now()")]
    public void TryBuildDefault_AcceptsOnlyFixedSafeExpressions(string expression)
    {
        var value = JsonSerializer.SerializeToElement(expression);

        Assert.True(SchemaDdlValidator.TryBuildDefault(value, out var sql, out _));
        Assert.Equal(expression, sql);
    }

    [Theory]
    [InlineData("now(); DROP TABLE usuarios")]
    [InlineData("a\\b")]
    [InlineData("line\nbreak")]
    public void TryBuildDefault_RejectsInjectionShapes(string text)
    {
        var value = JsonSerializer.SerializeToElement(text);

        Assert.False(SchemaDdlValidator.TryBuildDefault(value, out var sql, out var error));
        Assert.Null(sql);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryBuildDefault_AcceptsNumbersBooleansAndNull()
    {
        Assert.True(SchemaDdlValidator.TryBuildDefault(JsonSerializer.SerializeToElement(42), out var number, out _));
        Assert.Equal("42", number);

        Assert.True(SchemaDdlValidator.TryBuildDefault(JsonSerializer.SerializeToElement(true), out var boolean, out _));
        Assert.Equal("true", boolean);

        Assert.True(SchemaDdlValidator.TryBuildDefault(null, out var absent, out _));
        Assert.Null(absent);
    }

    [Fact]
    public void TryBuildDefault_RejectsObjectsAndArrays()
    {
        Assert.False(SchemaDdlValidator.TryBuildDefault(
            JsonSerializer.SerializeToElement(new { value = 1 }), out _, out _));
        Assert.False(SchemaDdlValidator.TryBuildDefault(
            JsonSerializer.SerializeToElement(new[] { 1, 2 }), out _, out _));
    }

    [Fact]
    public void TryValidate_CreateTableAcceptsValidDefinition()
    {
        var mutation = new CreateTableMutation("contactos", [Column("id", "uuid", primaryKey: true), Column("nombre")]);

        Assert.True(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("contactos; DROP TABLE usuarios")]
    [InlineData("Contactos")]
    [InlineData("pg_shadow")]
    public void TryValidate_CreateTableRejectsUnsafeTableName(string table)
    {
        var mutation = new CreateTableMutation(table, [Column("id", "uuid")]);

        Assert.False(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Contains("Table name", error);
    }

    [Fact]
    public void TryValidate_CreateTableRejectsUnsafeColumnName()
    {
        var mutation = new CreateTableMutation("contactos", [Column("id\"); DROP TABLE usuarios; --")]);

        Assert.False(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Contains("Column name", error);
    }

    [Fact]
    public void TryValidate_CreateTableRejectsUnsafeForeignKey()
    {
        var column = new ColumnDefinition(
            "proyecto_id", "uuid", true, false, false, null,
            new ColumnReference("proyectos\"; DROP TABLE usuarios; --", "id"));

        Assert.False(SchemaDdlValidator.TryValidate(new CreateTableMutation("contactos", [column]), out var error));
        Assert.Contains("Foreign key", error);
    }

    [Fact]
    public void TryValidate_CreateTableRejectsMultiplePrimaryKeys()
    {
        var mutation = new CreateTableMutation("contactos",
            [Column("a", "uuid", primaryKey: true), Column("b", "uuid", primaryKey: true)]);

        Assert.False(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Contains("primary key", error);
    }

    [Fact]
    public void TryValidate_IdentityRequiresIntegerTypeAndNoDefault()
    {
        var identity = new ColumnDefinition("id", "bigint", false, true, false, null, null, true);
        Assert.True(SchemaDdlValidator.TryValidate(
            new CreateTableMutation("contactos", [identity]), out _));

        var invalidType = new ColumnDefinition("id", "uuid", false, true, false, null, null, true);
        Assert.False(SchemaDdlValidator.TryValidate(
            new CreateTableMutation("contactos", [invalidType]), out var typeError));
        Assert.Contains("IDENTITY", typeError, StringComparison.Ordinal);

        var withDefault = new ColumnDefinition(
            "id", "bigint", false, true, false, "0", null, true);
        Assert.False(SchemaDdlValidator.TryValidate(
            new CreateTableMutation("contactos", [withDefault]), out var defaultError));
        Assert.Contains("IDENTITY", defaultError, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatements_EmitsSafeIdentityClause()
    {
        var identity = new ColumnDefinition("id", "bigint", false, true, false, null, null, true);
        var statements = ProjectSchemaAdmin.BuildStatements(
            new ProjectResourceNames(
                "domibase_test", "db_owner_test", "db_runtime_test", "anon_test", "authenticated_test"),
            new CreateTableMutation("contactos", [identity]));

        Assert.Contains("GENERATED BY DEFAULT AS IDENTITY", statements[0]);
    }

    [Fact]
    public void TryValidate_IndexRequiresValidColumnNames()
    {
        Assert.True(SchemaDdlValidator.TryValidate(
            new CreateIndexMutation("contactos", "contactos_email_idx", ["email"], false), out _));

        Assert.False(SchemaDdlValidator.TryValidate(
            new CreateIndexMutation("contactos", "idx", ["email); DROP TABLE usuarios"], false), out var error));
        Assert.Contains("Index column name", error);
    }

    [Fact]
    public void TryValidate_PolicyRejectsRawSqlExpression()
    {
        var mutation = Policy("contactos_select", "true; DROP TABLE usuarios");

        Assert.False(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Contains("safe templates", error);
    }

    [Fact]
    public void TryValidate_PolicyRejectsUnknownRoleAndCommand()
    {
        var mutation = new CreatePolicyMutation(
            "contactos",
            "politica",
            [PolicyCommand.Select],
            [SchemaFunctionalRole.Anon],
            "true",
            "true");

        Assert.True(SchemaDdlValidator.TryValidate(mutation, out _));

        var invalid = new CreatePolicyMutation(
            "contactos",
            "politica",
            [],
            [],
            "true",
            "true");

        Assert.False(SchemaDdlValidator.TryValidate(invalid, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_PolicyAcceptsTenantIsolationTemplate()
    {
        const string expression = "project_id = current_setting('domibase.project_id', true)::uuid";
        var mutation = new CreatePolicyMutation(
            "tenant_items", "tenant_select", [PolicyCommand.Select, PolicyCommand.Insert],
            [SchemaFunctionalRole.Anon], expression, expression);

        Assert.True(SchemaDdlValidator.TryValidate(mutation, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void BuildStatements_ComplementaryPoliciesDoNotRevokeEachOther()
    {
        var names = new ProjectResourceNames(
            "domibase_test", "db_owner_test", "db_runtime_test", "anon_test", "authenticated_test");

        var select = ProjectSchemaAdmin.BuildStatements(
            names,
            new CreatePolicyMutation("contactos", "anon_select", [PolicyCommand.Select],
                [SchemaFunctionalRole.Anon], "true", "true"));
        var insert = ProjectSchemaAdmin.BuildStatements(
            names,
            new CreatePolicyMutation("contactos", "anon_insert", [PolicyCommand.Insert],
                [SchemaFunctionalRole.Anon], "true", "true"));

        Assert.Contains("GRANT SELECT ON TABLE", string.Join("\n", select));
        Assert.Contains("GRANT INSERT ON TABLE", string.Join("\n", insert));
        Assert.DoesNotContain("REVOKE", string.Join("\n", select) + string.Join("\n", insert));
    }

    private static ColumnDefinition Column(
        string name,
        string type = "text",
        bool primaryKey = false) =>
        new(name, type, true, primaryKey, false, null, null);

    private static CreatePolicyMutation Policy(string name, string expression) =>
        new("contactos", name, [PolicyCommand.Select], [SchemaFunctionalRole.Anon], expression, expression);
}
