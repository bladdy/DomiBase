using DomiBase.Domain.Auditing;

namespace DomiBase.Application.Auditing;

/// <summary>One bounded page of audit rows plus the unpaged total for the active filter.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total);
