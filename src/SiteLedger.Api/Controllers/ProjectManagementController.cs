using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record AssignTaskRequest([StringLength(450)] string? UserId, Guid Version);

[ApiController]
[Route("api/v1/projects/{projectId:guid}")]
[Authorize(Policy = Permissions.ManageProjects)]
public sealed class ProjectManagementController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    private async Task<bool> CanAccess(Guid projectId, CancellationToken ct) =>
        await db.Projects.AnyAsync(p => p.Id == projectId, ct) &&
        (User.IsInRole(Roles.TenantAdmin) || await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct));

    [HttpPut("tasks/{id:guid}/assignee")]
    public async Task<IActionResult> Assign(Guid projectId, Guid id, AssignTaskRequest request, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (request.Version == Guid.Empty) return BadRequest();
        var task = await db.ProjectTasks.SingleOrDefaultAsync(t => t.ProjectId == projectId && t.Id == id, ct);
        if (task is null) return NotFound();
        if (task.RowVersion != request.Version) return Conflict(new { error = "The task changed. Reload and try again." });
        var assignee = string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId.Trim();
        if (assignee is not null && !(await db.Users.AnyAsync(u => u.Id == assignee && u.TenantId == current.TenantId && u.IsActive && u.EmailConfirmed, ct) &&
            await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == assignee, ct))) return NotFound();
        task.AssignedToUserId = assignee;
        Audit("task.assignee.change", "ProjectTask", id);
        try { await db.SaveChangesAsync(ct); return Ok(task); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The task changed. Reload and try again." }); }
    }

    [HttpDelete("tasks/{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid projectId, Guid id, [FromQuery] Guid version, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (version == Guid.Empty) return BadRequest();
        var task = await db.ProjectTasks.SingleOrDefaultAsync(t => t.ProjectId == projectId && t.Id == id, ct);
        if (task is null) return NotFound();
        if (task.RowVersion != version) return Conflict(new { error = "The task changed. Reload and try again." });
        db.ProjectTasks.Remove(task); Audit("task.delete", "ProjectTask", id);
        try { await db.SaveChangesAsync(ct); return NoContent(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The task changed. Reload and try again." }); }
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteProject(Guid projectId, [FromQuery] Guid version, CancellationToken ct)
    {
        if (version == Guid.Empty) return BadRequest();
        if (!await CanAccess(projectId, ct)) return NotFound();
        // Project writers currently do not all serialize their access checks with deletion.
        // Refuse deletion until those paths share a transaction and lock.
        return Conflict(new { error = "Project deletion is unavailable until concurrent record and file operations can be safely coordinated." });
    }

    private void Audit(string action, string type, Guid id) => db.AuditLogs.Add(new AuditLog { Action = action, EntityType = type, EntityId = id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
}

[ApiController]
[Route("api/v1/projects/{projectId:guid}")]
[Authorize(Policy = Permissions.CreateReport)]
public sealed class RecordDeletionController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpDelete("{kind:regex(^(reports|expenses)$)}/{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, string kind, Guid id, [FromQuery] Guid version, CancellationToken ct)
    {
        if (version == Guid.Empty) return BadRequest();
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct) || (!User.IsInRole(Roles.TenantAdmin) && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct))) return NotFound();
        Approvable? item = kind == "reports" ? await db.SiteReports.SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct) : await db.Expenses.SingleOrDefaultAsync(e => e.ProjectId == projectId && e.Id == id, ct);
        if (item is null) return NotFound();
        if (item.CreatedByUserId != current.UserId) return Forbid();
        if (item.Status is not (ApprovalStatus.Draft or ApprovalStatus.Rejected)) return Conflict(new { error = "Only draft or rejected records may be deleted." });
        if (item.RowVersion != version) return Conflict(new { error = "The record changed. Reload and try again." });
        if (await db.FileAssets.AnyAsync(f => f.ProjectId == projectId && (kind == "reports" ? f.SiteReportId == id : f.ExpenseId == id), ct)) return Conflict(new { error = "Remove attached files before deleting this record." });
        db.Remove(item);
        db.AuditLogs.Add(new AuditLog { Action = kind + ".delete", EntityType = kind, EntityId = id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
        try { await db.SaveChangesAsync(ct); return NoContent(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The record changed. Reload and try again." }); }
    }
}
