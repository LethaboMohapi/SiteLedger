using SiteLedger.Api.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{id:guid}")]
[Authorize(Policy = Permissions.ManageProjects)]
public sealed class ProjectEditingController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpPut]
    public async Task<IActionResult> Update(Guid id, [FromQuery] Guid version, CreateProjectRequest input, CancellationToken ct)
    {
        if (version == Guid.Empty || input.StartDate == default || input.EndDate < input.StartDate || !Enum.IsDefined(input.Status) ||
            input.Budget < 0 || input.Budget > 9999999999999999.99m || decimal.Round(input.Budget, 2) != input.Budget ||
            string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.ClientName) || string.IsNullOrWhiteSpace(input.SiteAddress)) return BadRequest();
        var tenantId = current.TenantId!.Value;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", ct);
        var item = await db.Projects.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (item is null || (!User.IsInRole(Roles.TenantAdmin) && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.UserId == current.UserId, ct))) return NotFound();
        if (item.RowVersion != version) return Conflict(new { error = "Project changed. Reload and retry." });
        if (item.Status != ProjectStatus.Active && input.Status == ProjectStatus.Active)
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            try { PlanRules.EnsureProjectCapacity(tenant.Plan, await db.Projects.CountAsync(p => p.Status == ProjectStatus.Active, ct)); }
            catch (PlanLimitException ex) { return Conflict(new { error = ex.Message }); }
        }
        item.Name = input.Name.Trim(); item.ClientName = input.ClientName.Trim(); item.SiteAddress = input.SiteAddress.Trim();
        item.Budget = input.Budget; item.StartDate = input.StartDate; item.EndDate = input.EndDate; item.Status = input.Status;
        AuditEvents.Add(db, current, "project.update", nameof(Project), item.Id);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Ok(item); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Project changed. Reload and retry." }); }
    }
}
