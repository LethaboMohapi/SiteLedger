using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Policy = Permissions.ReadProjects)]
public sealed class DashboardController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var projects = db.Projects.AsNoTracking();
        if (!User.IsInRole(Roles.TenantAdmin))
            projects = projects.Where(p => db.ProjectMembers.Any(m => m.ProjectId == p.Id && m.UserId == current.UserId));
        var visibleTasks = db.ProjectTasks.Where(t => projects.Any(p => p.Id == t.ProjectId));
        var expenses = db.Expenses.Where(e => e.Status == ApprovalStatus.Approved && projects.Any(p => p.Id == e.ProjectId));
        return Ok(new {
            projects = await projects.CountAsync(ct),
            activeProjects = await projects.CountAsync(p => p.Status == ProjectStatus.Active, ct),
            openTasks = await visibleTasks.CountAsync(t => t.Status != SiteLedger.Core.TaskStatus.Done, ct),
            approvedExpenses = await expenses.SumAsync(e => (decimal?)e.Amount, ct) ?? 0m
        });
    }
}
