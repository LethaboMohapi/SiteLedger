using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record ProjectMemberInput([Required, StringLength(450)] string UserId, [Required, StringLength(100)] string RoleOnProject);

[ApiController]
[Route("api/v1/projects/{projectId:guid}/members")]
[Authorize(Policy = Permissions.ManageUsers)]
public sealed class ProjectMembersController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return NotFound();
        var members = await (from member in db.ProjectMembers.AsNoTracking()
                             join user in db.Users.AsNoTracking() on member.UserId equals user.Id
                             where member.ProjectId == projectId && user.TenantId == current.TenantId
                             orderby user.FullName, user.Id
                             select new { member.UserId, user.FullName, user.Email, user.IsActive, member.RoleOnProject }).ToListAsync(ct);
        return Ok(members);
    }

    [HttpPut]
    public async Task<IActionResult> AddOrRestore(Guid projectId, ProjectMemberInput input, CancellationToken ct)
    {
        var tenantId = current.TenantId!.Value;
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return NotFound();
        if (string.IsNullOrWhiteSpace(input.UserId) || string.IsNullOrWhiteSpace(input.RoleOnProject)) return BadRequest();
        var userExists = await db.Users.AnyAsync(u => u.Id == input.UserId && u.TenantId == tenantId && u.IsActive, ct);
        if (!userExists) return NotFound();
        // Ignore filters only with an explicit tenant discriminator, to restore a soft-deleted membership.
        var member = await db.ProjectMembers.IgnoreQueryFilters().SingleOrDefaultAsync(m => m.TenantId == tenantId && m.ProjectId == projectId && m.UserId == input.UserId, ct);
        if (member is null)
        {
            member = new ProjectMember { ProjectId = projectId, UserId = input.UserId, RoleOnProject = input.RoleOnProject.Trim() };
            db.ProjectMembers.Add(member);
        }
        else { member.IsDeleted = false; member.RoleOnProject = input.RoleOnProject.Trim(); }
        db.AuditLogs.Add(new AuditLog { Action = "project.member.put", EntityType = "Project", EntityId = projectId, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow, Details = System.Text.Json.JsonSerializer.Serialize(new { input.UserId }) });
        try { await db.SaveChangesAsync(ct); return Ok(new { member.UserId, member.RoleOnProject }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Membership changed; reload and retry." }); }
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Remove(Guid projectId, string userId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return NotFound();
        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null) return NotFound();
        db.ProjectMembers.Remove(member);
        db.AuditLogs.Add(new AuditLog { Action = "project.member.remove", EntityType = "Project", EntityId = projectId, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow, Details = System.Text.Json.JsonSerializer.Serialize(new { userId }) });
        try { await db.SaveChangesAsync(ct); return NoContent(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Membership changed; reload and retry." }); }
    }
}

[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = Permissions.ManageUsers)]
public sealed class TenantUsersController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await db.Users.AsNoTracking()
        .Where(u => u.TenantId == current.TenantId)
        .OrderBy(u => u.FullName).ThenBy(u => u.Id)
        .Select(u => new { u.Id, u.FullName, u.Email, u.IsActive }).Take(500).ToListAsync(ct));
}
