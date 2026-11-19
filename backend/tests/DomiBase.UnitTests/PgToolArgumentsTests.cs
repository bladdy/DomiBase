using DomiBase.Application.Backups;

namespace DomiBase.UnitTests;

/// <summary>
/// Dump/restore arguments and diagnostics must never carry credentials or unvalidated
/// identifiers, even when the caller supplies hostile metadata.
/// </summary>
public class PgToolArgumentsTests
{
    private const string Password = "unit-test-password-do-not-log";

    private static readonly PgConnectionSettings Connection =
        new("127.0.0.1", 5432, "domibase_admin", Password);

    [Fact]
    public void BuildDump_UsesCustomFormatAndNeverIncludesThePassword()
    {
        var arguments = PgToolArguments.BuildDump(Connection, "domibase_unit_backup");

        Assert.Contains("--format=custom", arguments);
        Assert.Contains("--dbname=domibase_unit_backup", arguments);
        Assert.Contains("--host=127.0.0.1", arguments);
        Assert.Contains("--port=5432", arguments);
        Assert.Contains("--username=domibase_admin", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains("PGPASSWORD", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRestore_IsAtomicAndDropsExistingObjects()
    {
        var arguments = PgToolArguments.BuildRestore(Connection, "domibase_unit_backup");

        Assert.Contains("--dbname=domibase_unit_backup", arguments);
        Assert.Contains("--clean", arguments);
        Assert.Contains("--if-exists", arguments);
        Assert.Contains("--single-transaction", arguments);
        Assert.Contains("--exit-on-error", arguments);
        Assert.Contains("--no-owner", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(Password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Domibase_Upper")]
    [InlineData("1domibase")]
    [InlineData("domibase;DROP TABLE users")]
    [InlineData("domibase\" --")]
    [InlineData("domibase\nother")]
    public void BuildDump_RejectsUnsafeDatabaseNames(string database)
    {
        Assert.Throws<ArgumentException>(() => PgToolArguments.BuildDump(Connection, database));
    }

    [Theory]
    [InlineData("")]
    [InlineData("domibase_owner; NOINHERIT")]
    [InlineData("domibase-owner")]
    public void BuildRoleOption_RejectsUnsafeRoleNames(string role)
    {
        Assert.Throws<ArgumentException>(() => PgToolArguments.BuildRoleOption(role));
    }

    [Fact]
    public void BuildRoleOption_UsesBareValidatedIdentifier()
    {
        Assert.Equal("-c role=domibase_unit_backup_owner", PgToolArguments.BuildRoleOption("domibase_unit_backup_owner"));
    }

    [Fact]
    public void Redact_RemovesEveryCredentialOccurrenceAndBoundsTheMessage()
    {
        var message = $"failed for password {Password} and user {Password}";
        var redacted = PgToolError.Redact(message, Password);

        Assert.DoesNotContain(Password, redacted, StringComparison.Ordinal);
        Assert.Equal(2, redacted.Split("[redacted]").Length - 1);
        Assert.Equal(PgToolError.MaxLength, PgToolError.Redact(new string('x', 5000), Password).Length);
    }

    [Fact]
    public void Redact_HandlesMissingDiagnostics()
    {
        Assert.Equal(string.Empty, PgToolError.Redact(null, Password));
        Assert.Equal("plain text", PgToolError.Redact("plain text", string.Empty));
    }
}
