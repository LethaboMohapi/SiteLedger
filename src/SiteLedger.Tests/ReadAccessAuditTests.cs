using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using SiteLedger.Api.Auditing;

namespace SiteLedger.Tests;

public sealed class ReadAccessAuditTests
{
    private sealed class Writer : IReadAccessAuditWriter
    {
        public List<ReadAccessEvent> Events { get; } = [];
        public bool Fail { get; set; }
        public Task WriteAsync(ReadAccessEvent entry, CancellationToken ct)
        {
            if (Fail) throw new IOException("Unavailable test store");
            Events.Add(entry); return Task.CompletedTask;
        }
    }
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext(); context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?token=private-value");
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "user"), new Claim("tenant_id", Guid.NewGuid().ToString()) }, "test"));
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ControllerActionDescriptor
        {
            ControllerName = "Exports", ActionName = "Projects", AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = "api/v1/exports/projects.csv" }
        }), "test"));
        return context;
    }

    [Fact]
    public async Task AuditFailurePreventsTheReadFromExecuting()
    {
        var called = false; var context = Context(); var writer = new Writer { Fail = true };
        var middleware = new ReadAccessAuditMiddleware(_ => { called = true; return Task.CompletedTask; }, NullLogger<ReadAccessAuditMiddleware>.Instance);
        await middleware.InvokeAsync(context, writer);
        Assert.False(called); Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task DeniedReadRecordsBothStagesAndNoQueryValues()
    {
        var context = Context(); var writer = new Writer();
        var middleware = new ReadAccessAuditMiddleware(ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; }, NullLogger<ReadAccessAuditMiddleware>.Instance);
        await middleware.InvokeAsync(context, writer);
        Assert.Equal(2, writer.Events.Count); Assert.Equal(writer.Events[0].RequestId, writer.Events[1].RequestId);
        Assert.Equal("export", writer.Events[0].Operation); Assert.Equal(403, writer.Events[1].StatusCode);
        Assert.All(writer.Events, entry => Assert.DoesNotContain("private-value", entry.Route));
    }

    [Fact]
    public async Task HandlerExceptionIsRecordedAsFailureAndStillPropagates()
    {
        var context = Context(); var writer = new Writer();
        var middleware = new ReadAccessAuditMiddleware(_ => throw new InvalidOperationException("Test error"), NullLogger<ReadAccessAuditMiddleware>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, writer));
        Assert.Equal(500, writer.Events.Last().StatusCode);
    }
}
