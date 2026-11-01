using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

public static class WorkflowNotifications
{
    public static async Task Queue(SiteLedgerDbContext db, ICurrentTenant current, IDataProtectionProvider protection, Approvable record, bool submitted, CancellationToken ct)
    {
        var tenantId = current.TenantId!.Value;
        var recipients = db.Users.AsNoTracking().Where(u => u.TenantId == tenantId && u.IsActive && u.EmailConfirmed && u.Email != null && u.Id != current.UserId);
        if (submitted)
            recipients = recipients.Where(u => (from link in db.UserRoles join role in db.Roles on link.RoleId equals role.Id
                where link.UserId == u.Id && (role.NormalizedName == "TENANTADMIN" || (role.NormalizedName == "MANAGER" && db.ProjectMembers.Any(m => m.ProjectId == record.ProjectId && m.UserId == u.Id))) select link.UserId).Any());
        else recipients = recipients.Where(u => u.Id == record.CreatedByUserId);
        var addresses = await recipients.Select(u => u.Email!).Distinct().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var address in addresses)
        {
            var description = record is SiteReport ? "Site report" : "Expense";
            var message = new PendingEmail(address, $"SiteLedger: {description} {record.Status.ToString().ToLowerInvariant()}",
                $"{description} {record.Id} in project {record.ProjectId} is now {record.Status}. Sign in to SiteLedger to view it.");
            db.EmailOutbox.Add(new EmailOutboxMessage { CreatedAt = now, NextAttemptAt = now, ExpiresAt = now.AddHours(23), ProtectedPayload = protection.CreateProtector("SiteLedger.EmailOutbox.v1").Protect(JsonSerializer.Serialize(message)) });
        }
    }
}
