using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/exports")]
[Authorize(Policy = Permissions.Export)]
public sealed class ExportsController(SiteLedgerDbContext db, ICurrentTenant current) : ControllerBase
{
    [HttpGet("projects.csv")]
    public async Task<IActionResult> Projects(CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == current.TenantId, ct);
        if (tenant is null || !PlanLimits.For(tenant.Plan).CsvExport)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "CSV export requires a Pro or Enterprise plan.");

        var query = db.Projects.AsNoTracking();
        if (!User.IsInRole(Roles.TenantAdmin))
            query = query.Where(p => db.ProjectMembers.Any(m => m.ProjectId == p.Id && m.UserId == current.UserId));

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = "attachment; filename=siteledger-projects.csv";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(true), 4096, leaveOpen: true);
        await writer.WriteLineAsync("Id,Name,Client,Site address,Budget,Start date,End date,Status".AsMemory(), ct);
        await foreach (var p in query.OrderBy(p => p.Name).ThenBy(p => p.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            var line = string.Join(',', Csv(p.Id.ToString()), Csv(p.Name), Csv(p.ClientName), Csv(p.SiteAddress),
                p.Budget.ToString("0.00", CultureInfo.InvariantCulture), Csv(p.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(p.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""), Csv(p.Status.ToString()));
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
        return new EmptyResult();
    }

    [HttpGet("projects/{projectId:guid}/{kind:regex(^(reports|expenses)$)}.csv")]
    public async Task<IActionResult> Records(Guid projectId, string kind, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == current.TenantId, ct);
        if (tenant is null || !PlanLimits.For(tenant.Plan).CsvExport)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "CSV export requires a Pro or Enterprise plan.");
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct) ||
            (!User.IsInRole(Roles.TenantAdmin) && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct))) return NotFound();
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=siteledger-{kind}.csv";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(true), 4096, leaveOpen: true);
        if (kind == "reports")
        {
            await writer.WriteLineAsync("Id,Date,Weather,Workers,Work summary,Issues,Status".AsMemory(), ct);
            await foreach (var item in db.SiteReports.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.ReportDate).ThenBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
            {
                var line = string.Join(',', Csv(item.Id.ToString()), Csv(item.ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Csv(item.Weather), item.WorkersOnSite.ToString(CultureInfo.InvariantCulture), Csv(item.WorkSummary), Csv(item.Issues), Csv(item.Status.ToString()));
                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
        }
        else
        {
            await writer.WriteLineAsync("Id,Date,Category,Amount,Supplier,Status".AsMemory(), ct);
            await foreach (var item in db.Expenses.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.ExpenseDate).ThenBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
            {
                var line = string.Join(',', Csv(item.Id.ToString()), Csv(item.ExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Csv(item.Category.ToString()), item.Amount.ToString("0.00", CultureInfo.InvariantCulture), Csv(item.Supplier), Csv(item.Status.ToString()));
                await writer.WriteLineAsync(line.AsMemory(), ct);
            }
        }
        await writer.FlushAsync(ct);
        return new EmptyResult();
    }

    private static string Csv(string value)
    {
        var trimmed = value.TrimStart();
        if ((value.Length > 0 && value[0] is '\t' or '\r' or '\n') || (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
