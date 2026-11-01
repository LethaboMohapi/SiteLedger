using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auditing;

public static class AuditEvents
{
    // Audit data and the operation are persisted in the same SaveChanges call.
    // Do not include passwords, tokens, file contents, or other user-provided text.
    public static void Add(SiteLedgerDbContext db, ICurrentTenant current, string action, string entityType, Guid entityId)
    {
        var actor = current.UserId ?? throw new InvalidOperationException("An authenticated actor is required.");
        db.AuditLogs.Add(new AuditLog { Action = action, EntityType = entityType, EntityId = entityId,
            UserId = actor, Timestamp = DateTimeOffset.UtcNow });
    }
}
