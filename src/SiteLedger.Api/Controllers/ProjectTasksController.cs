using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Api.Auditing;
using SiteLedger.Infrastructure;
using WorkStatus = SiteLedger.Core.TaskStatus;

namespace SiteLedger.Api.Controllers;

public sealed record TaskInput([Required, StringLength(200)] string Title, [StringLength(10000)] string Description,
    DateOnly? DueDate, TaskPriority Priority, WorkStatus Status, [Range(0, 100)] int PercentComplete);

[ApiController]
[Route("api/v1/projects/{projectId:guid}/tasks")]
[Authorize(Policy = Permissions.ReadProjects)]
public sealed class ProjectTasksController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    private async Task<bool> CanAccess(Guid projectId, CancellationToken ct) =>
        await db.Projects.AnyAsync(p => p.Id == projectId, ct) &&
        (User.IsInRole(Roles.TenantAdmin) || await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct));

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        return Ok(await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId).OrderBy(t => t.DueDate).ThenBy(t => t.Id).Take(100).ToListAsync(ct));
    }

    [HttpGet("assignees")]
    [Authorize(Policy = Permissions.ManageProjects)]
    public async Task<IActionResult> Assignees(Guid projectId, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var result = await (from member in db.ProjectMembers.AsNoTracking()
                            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
                            where member.ProjectId == projectId && user.TenantId == current.TenantId && user.IsActive && user.EmailConfirmed
                            orderby user.FullName, user.Id
                            select new { user.Id, user.FullName }).ToListAsync(ct);
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ManageProjects)]
    public async Task<IActionResult> Create(Guid projectId, TaskInput input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (!Valid(input)) return BadRequest();
        var task = new ProjectTask { ProjectId = projectId, Title = input.Title.Trim(), Description = input.Description?.Trim() ?? "", DueDate = input.DueDate,
            Priority = input.Priority, Status = input.Status, PercentComplete = input.PercentComplete };
        db.ProjectTasks.Add(task);
        AuditEvents.Add(db, current, "task.create", nameof(ProjectTask), task.Id);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, task);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ManageProjects)]
    public async Task<IActionResult> Update(Guid projectId, Guid id, TaskInput input, [FromQuery] Guid version, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (!Valid(input) || version == Guid.Empty) return BadRequest();
        var task = await db.ProjectTasks.SingleOrDefaultAsync(t => t.ProjectId == projectId && t.Id == id, ct);
        if (task is null) return NotFound();
        if (task.RowVersion != version) return Conflict(new { error = "The task changed. Reload and try again." });
        task.Title = input.Title.Trim(); task.Description = input.Description?.Trim() ?? ""; task.DueDate = input.DueDate;
        task.Priority = input.Priority; task.Status = input.Status; task.PercentComplete = input.PercentComplete;
        AuditEvents.Add(db, current, "task.update", nameof(ProjectTask), task.Id);
        try { await db.SaveChangesAsync(ct); return Ok(task); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The task changed. Reload and try again." }); }
    }

    private static bool Valid(TaskInput input) => !string.IsNullOrWhiteSpace(input.Title) && Enum.IsDefined(input.Priority) && Enum.IsDefined(input.Status)
        && input.PercentComplete is >= 0 and <= 100 && (input.Status != WorkStatus.Done || input.PercentComplete == 100);
}
