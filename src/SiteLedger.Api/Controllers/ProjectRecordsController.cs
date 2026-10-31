using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record NewReport([Required] DateOnly ReportDate, [Required, StringLength(100)] string Weather,
    [Range(0, 100000)] int WorkersOnSite, [Required, StringLength(10000)] string WorkSummary,
    [StringLength(10000)] string Issues);
public sealed record NewExpense([Required] DateOnly ExpenseDate, ExpenseCategory Category,
    decimal Amount, [Required, StringLength(200)] string Supplier);
public sealed record ReviewRequest(bool Approve, [Required, StringLength(2000)] string Comment);

[ApiController]
[Route("api/v1/projects/{projectId:guid}")]
[Authorize(Policy = Permissions.ReadProjects)]
public sealed class ProjectRecordsController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    private async Task<bool> CanAccess(Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return false;
        return User.IsInRole(Roles.TenantAdmin) || await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct);
    }

    [HttpGet("reports")]
    public async Task<IActionResult> Reports(Guid projectId, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var query = db.SiteReports.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (User.IsInRole(Roles.Viewer)) query = query.Where(x => x.Status == ApprovalStatus.Approved);
        return Ok(await query.OrderByDescending(x => x.ReportDate).ThenBy(x => x.Id).Take(100).ToListAsync(ct));
    }

    [HttpPost("reports")]
    [Authorize(Policy = Permissions.CreateReport)]
    public async Task<IActionResult> CreateReport(Guid projectId, NewReport input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (input.ReportDate == default || string.IsNullOrWhiteSpace(input.Weather) || string.IsNullOrWhiteSpace(input.WorkSummary)) return BadRequest();
        var report = new SiteReport { ProjectId = projectId, CreatedByUserId = current.UserId!, ReportDate = input.ReportDate,
            Weather = input.Weather.Trim(), WorkersOnSite = input.WorkersOnSite, WorkSummary = input.WorkSummary.Trim(), Issues = input.Issues?.Trim() ?? "" };
        db.SiteReports.Add(report);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, report);
    }

    [HttpGet("expenses")]
    public async Task<IActionResult> Expenses(Guid projectId, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var query = db.Expenses.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (User.IsInRole(Roles.Viewer)) query = query.Where(x => x.Status == ApprovalStatus.Approved);
        return Ok(await query.OrderByDescending(x => x.ExpenseDate).ThenBy(x => x.Id).Take(100).ToListAsync(ct));
    }

    [HttpPost("expenses")]
    [Authorize(Policy = Permissions.CreateReport)]
    public async Task<IActionResult> CreateExpense(Guid projectId, NewExpense input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        if (input.ExpenseDate == default || !Enum.IsDefined(input.Category) || input.Amount <= 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount || string.IsNullOrWhiteSpace(input.Supplier)) return BadRequest();
        var expense = new Expense { ProjectId = projectId, CreatedByUserId = current.UserId!, ExpenseDate = input.ExpenseDate,
            Category = input.Category, Amount = input.Amount, Supplier = input.Supplier.Trim() };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, expense);
    }

    [HttpPut("reports/{id:guid}")]
    [Authorize(Policy = Permissions.CreateReport)]
    public async Task<IActionResult> EditReport(Guid projectId, Guid id, [FromQuery] Guid version, NewReport input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var item = await db.SiteReports.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);
        if (item is null) return NotFound();
        if (item.CreatedByUserId != current.UserId) return Forbid();
        if (item.Status is not (ApprovalStatus.Draft or ApprovalStatus.Rejected)) return Conflict(new { error = "Only draft or rejected records can be edited." });
        if (version == Guid.Empty || input.ReportDate == default || string.IsNullOrWhiteSpace(input.Weather) || string.IsNullOrWhiteSpace(input.WorkSummary)) return BadRequest();
        if (item.RowVersion != version) return Conflict(new { error = "The record changed. Reload and try again." });
        item.ReportDate = input.ReportDate; item.Weather = input.Weather.Trim(); item.WorkersOnSite = input.WorkersOnSite;
        item.WorkSummary = input.WorkSummary.Trim(); item.Issues = input.Issues?.Trim() ?? "";
        try { await db.SaveChangesAsync(ct); return Ok(item); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The record changed. Reload and try again." }); }
    }

    [HttpPut("expenses/{id:guid}")]
    [Authorize(Policy = Permissions.CreateReport)]
    public async Task<IActionResult> EditExpense(Guid projectId, Guid id, [FromQuery] Guid version, NewExpense input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var item = await db.Expenses.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);
        if (item is null) return NotFound();
        if (item.CreatedByUserId != current.UserId) return Forbid();
        if (item.Status is not (ApprovalStatus.Draft or ApprovalStatus.Rejected)) return Conflict(new { error = "Only draft or rejected records can be edited." });
        if (version == Guid.Empty || input.ExpenseDate == default || !Enum.IsDefined(input.Category) || input.Amount <= 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount || string.IsNullOrWhiteSpace(input.Supplier)) return BadRequest();
        if (item.RowVersion != version) return Conflict(new { error = "The record changed. Reload and try again." });
        item.ExpenseDate = input.ExpenseDate; item.Category = input.Category; item.Amount = input.Amount; item.Supplier = input.Supplier.Trim();
        try { await db.SaveChangesAsync(ct); return Ok(item); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The record changed. Reload and try again." }); }
    }

    [HttpPost("{kind:regex(^(reports|expenses)$)}/{id:guid}/submit")]
    [Authorize(Policy = Permissions.CreateReport)]
    public async Task<IActionResult> Submit(Guid projectId, string kind, Guid id, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var item = await Find(kind, projectId, id, ct);
        if (item is null) return NotFound();
        if (item.CreatedByUserId != current.UserId) return Forbid();
        try { item.Submit(); await db.SaveChangesAsync(ct); return Ok(item); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The record changed. Reload and try again." }); }
    }

    [HttpPost("{kind:regex(^(reports|expenses)$)}/{id:guid}/review")]
    [Authorize(Policy = Permissions.Review)]
    public async Task<IActionResult> Review(Guid projectId, string kind, Guid id, ReviewRequest input, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var item = await Find(kind, projectId, id, ct);
        if (item is null) return NotFound();
        if (item.CreatedByUserId == current.UserId) return Forbid();
        if (string.IsNullOrWhiteSpace(input.Comment)) return BadRequest();
        try { item.Review(input.Approve, current.UserId!, input.Comment, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return Ok(item); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The record changed. Reload and try again." }); }
    }

    private async Task<Approvable?> Find(string kind, Guid projectId, Guid id, CancellationToken ct) => kind switch
    {
        "reports" => await db.SiteReports.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct),
        "expenses" => await db.Expenses.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct),
        _ => null
    };
}
