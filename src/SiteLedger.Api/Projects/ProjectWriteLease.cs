using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Projects;

public static class ProjectWriteLease
{
    // Every path creating a project child takes this lock before checking project access.
    public static async Task<IDbContextTransaction> Begin(SiteLedgerDbContext db, Guid tenantId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", ct); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
