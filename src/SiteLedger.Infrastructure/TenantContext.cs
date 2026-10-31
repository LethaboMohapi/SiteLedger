using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SiteLedger.Infrastructure;

public interface ICurrentTenant { Guid? TenantId { get; } string? UserId { get; } }

// An absent or invalid claim resolves to no tenant. Tenant IDs must never be
// accepted from route parameters, headers, or request bodies.
public sealed class CurrentTenant(IHttpContextAccessor accessor) : ICurrentTenant
{
    public Guid? TenantId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("tenant_id"), out var id) && id != Guid.Empty ? id : null;
    public string? UserId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
}
