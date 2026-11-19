using DomiBase.Application.Auditing;

namespace DomiBase.UnitTests;

public class AuditRedactorTests
{
    [Fact]
    public void Redact_MasksSecretShapesInJsonDetails()
    {
        var details = """
            {"ProjectId":"11111111-1111-7111-8111-111111111111","apiKey":"db_public_supersecretvalue123","password":"hunter2","SecretHash":"c2VjcmV0"}
            """;

        var redacted = AuditRedactor.Redact(details)!;

        Assert.Contains("11111111-1111-7111-8111-111111111111", redacted);
        Assert.DoesNotContain("db_public_supersecretvalue123", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("c2VjcmV0", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void Redact_MasksAssignmentsAndBearerTokens()
    {
        var text = "PGPASSWORD=admin-secret password: another-secret Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signaturepart";

        var redacted = AuditRedactor.Redact(text)!;

        Assert.DoesNotContain("admin-secret", redacted);
        Assert.DoesNotContain("another-secret", redacted);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void Redact_KeepsNullAndShortNonSecretValues()
    {
        Assert.Null(AuditRedactor.Redact(null));
        Assert.Equal("schema.public.table=contactos", AuditRedactor.Redact("schema.public.table=contactos"));
    }

    [Fact]
    public void Redact_BoundsVeryLongDiagnostics()
    {
        var value = new string('x', 9000);

        var redacted = AuditRedactor.Redact(value)!;

        Assert.Equal(4001, redacted.Length);
        Assert.EndsWith("…", redacted);
    }
}
