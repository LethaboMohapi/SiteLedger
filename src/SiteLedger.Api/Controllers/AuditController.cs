using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = Permissions.ReadAudit)]
public sealed class AuditController(SiteLedgerDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery, Range(1, 100000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 50, CancellationToken ct = default)
    {
        var events = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Action, x.EntityType, x.EntityId, x.UserId, x.Timestamp, x.Details })
            .ToListAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(events);
    }
}
