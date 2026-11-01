using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auditing;

public sealed record ReadAccessEvent(Guid TenantId, string UserId, Guid RequestId,
    string Operation, string Route, string Method, string Stage, int? StatusCode);

public interface IReadAccessAuditWriter
{
    Task WriteAsync(ReadAccessEvent entry, CancellationToken ct);
}

public sealed class ReadAccessAuditWriter(DbContextOptions<SiteLedgerDbContext> options) : IReadAccessAuditWriter
{
    private sealed record Subject(Guid? TenantId, string? UserId) : ICurrentTenant;

    public async Task WriteAsync(ReadAccessEvent entry, CancellationToken ct)
    {
        // Separate tracking prevents a read audit from flushing pending controller changes.
        await using var db = new SiteLedgerDbContext(options, new Subject(entry.TenantId, entry.UserId));
        db.AuditLogs.Add(new AuditLog
        {
            Action = entry.Operation + "." + entry.Stage,
            EntityType = "ApiRequest", EntityId = entry.RequestId,
            UserId = entry.UserId, Timestamp = DateTimeOffset.UtcNow,
            Details = JsonSerializer.Serialize(new { entry.Route, entry.Method, entry.StatusCode })
        });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ReadAccessAuditMiddleware(RequestDelegate next, ILogger<ReadAccessAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IReadAccessAuditWriter audit)
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (action is null || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
            context.User.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId) ||
            !Guid.TryParse(context.User.FindFirstValue("tenant_id"), out var tenantId) || tenantId == Guid.Empty)
        {
            await next(context);
            return;
        }

        var operation = action.ControllerName is "Exports" or "PdfExports" ? "export" :
            action.ControllerName == "ProjectFiles" && action.ActionName == "Download" ? "file.download" : "read";
        // The route template is application metadata. Never record URL/query values, headers or bodies.
        var entry = new ReadAccessEvent(tenantId, userId, Guid.NewGuid(), operation,
            action.AttributeRouteInfo?.Template ?? action.ControllerName + "/" + action.ActionName,
            context.Request.Method, "started", null);
        try { await audit.WriteAsync(entry, context.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Read access denied because its audit record could not be persisted.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 503, Title = "Access auditing is temporarily unavailable. Please retry."
            }, context.RequestAborted);
            return;
        }

        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            try
            {
                // Retain a completion event even if the caller disconnected. The durable started
                // event remains evidence when a process crash prevents completion altogether.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await audit.WriteAsync(entry with { Stage = "completed", StatusCode = failed ? 500 : context.Response.StatusCode }, timeout.Token);
            }
            catch (Exception ex) { logger.LogCritical(ex, "Read access completion audit failed for {AuditRequestId}.", entry.RequestId); }
        }
    }
}
