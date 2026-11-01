using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Api.Exports;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/exports/projects/{projectId:guid}/reports")]
[Authorize(Policy = Permissions.Export)]
public sealed class PdfExportsController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpGet("{reportId:guid}.pdf")]
    public async Task<IActionResult> Report(Guid projectId, Guid reportId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == current.TenantId, ct);
        if (tenant is null || !PlanLimits.For(tenant.Plan).PdfReports)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "PDF reports require a Pro or Enterprise plan.");
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null || (!User.IsInRole(Roles.TenantAdmin) && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct))) return NotFound();
        var report = await db.SiteReports.AsNoTracking().SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Id == reportId, ct);
        if (report is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(PdfReportRenderer.Render(project, report), "application/pdf", $"site-report-{reportId:N}.pdf");
    }
}
