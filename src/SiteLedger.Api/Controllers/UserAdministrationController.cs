using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record UserAdministrationRequest([Required, StringLength(200)] string FullName, [Required, StringLength(50)] string Role, bool IsActive);

[ApiController]
[Route("api/v1/users/{id}")]
[Authorize(Policy = Permissions.ManageUsers)]
public sealed class UserAdministrationController(SiteLedgerDbContext db, ICurrentTenant current, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles) : ControllerBase
{
    private static readonly string[] AllowedRoles = [Roles.TenantAdmin, Roles.Manager, Roles.Member, Roles.Viewer];

    [HttpPut]
    public async Task<IActionResult> Update(string id, UserAdministrationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) || !AllowedRoles.Contains(request.Role, StringComparer.Ordinal)) return BadRequest();
        var tenantId = current.TenantId!.Value;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize changes affecting quotas and the last administrator invariant.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == tenantId, ct);
        if (user is null) return NotFound();
        var oldRoles = await users.GetRolesAsync(user);
        if (oldRoles.Any(r => !AllowedRoles.Contains(r, StringComparer.Ordinal))) return Forbid();
        if (user.IsActive && oldRoles.Contains(Roles.TenantAdmin) && (!request.IsActive || request.Role != Roles.TenantAdmin))
        {
            var activeAdmins = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
                where u.TenantId == tenantId && u.IsActive && r.NormalizedName == "TENANTADMIN" select u.Id).Distinct().CountAsync(ct);
            if (activeAdmins <= 1) return Conflict(new { error = "A tenant must retain an active administrator." });
        }
        if (request.IsActive)
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            var activeUsers = await db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive && u.Id != id, ct);
            var viewers = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
                where u.TenantId == tenantId && u.IsActive && u.Id != id && r.NormalizedName == "VIEWER" select u.Id).Distinct().CountAsync(ct);
            try { PlanRules.EnsureUserCapacity(tenant.Plan, request.Role == Roles.Viewer, activeUsers - viewers, viewers); }
            catch (PlanLimitException ex) { return Conflict(new { error = ex.Message }); }
        }
        if (!await roles.RoleExistsAsync(request.Role))
        {
            var created = await roles.CreateAsync(new IdentityRole(request.Role));
            if (!created.Succeeded) return Conflict(new { error = "Role initialization failed. Retry." });
        }
        if (oldRoles.Count > 0 && !(await users.RemoveFromRolesAsync(user, oldRoles)).Succeeded) return Conflict();
        if (!(await users.AddToRoleAsync(user, request.Role)).Succeeded) return Conflict();
        user.FullName = request.FullName.Trim(); user.IsActive = request.IsActive;
        if (!(await users.UpdateAsync(user)).Succeeded || !(await users.UpdateSecurityStampAsync(user)).Succeeded) return Conflict();
        db.AuditLogs.Add(new AuditLog { Action = "user.update", EntityType = "User", EntityId = Guid.Empty, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow, Details = System.Text.Json.JsonSerializer.Serialize(new { userId = id, request.Role, request.IsActive }) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Ok(new { user.Id, user.FullName, user.Email, user.IsActive, role = request.Role });
    }
}
