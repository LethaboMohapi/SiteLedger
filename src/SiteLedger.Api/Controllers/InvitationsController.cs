using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Api.Auth;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record IssueInvitation([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(50)] string Role);
public sealed record AcceptInvitation(Guid TenantId, Guid InvitationId, [Required, MaxLength(100)] string Token,
    [Required, StringLength(200)] string FullName, [Required, MaxLength(256)] string Password);

[ApiController]
[Route("api/v1/invitations")]
public sealed class InvitationsController(SiteLedgerDbContext db, ICurrentTenant current, UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles, IAccountEmailSender sender, IConfiguration configuration, ILogger<InvitationsController> logger) : ControllerBase
{
    private static readonly string[] AllowedRoles = [Roles.TenantAdmin, Roles.Manager, Roles.Member, Roles.Viewer];
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [HttpPost]
    [Authorize(Policy = Permissions.ManageUsers)]
    public async Task<IActionResult> Issue(IssueInvitation input, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(input.Role, StringComparer.Ordinal)) return BadRequest();
        var baseUrl = configuration["Account:PublicBaseUrl"]?.TrimEnd('/');
        if (!sender.IsConfigured || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return Problem(statusCode: 503, title: "Invitation email is not configured.");
        var address = input.Email.Trim();
        if (await users.FindByEmailAsync(address) is not null) return Conflict(new { error = "This account cannot be invited." });
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var invitation = new Invitation { Email = address, Role = input.Role, TokenHash = Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(2) };
        db.Invitations.Add(invitation);
        db.AuditLogs.Add(new AuditLog { Action = "invitation.issue", EntityType = "Invitation", EntityId = invitation.Id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        var body = $"You have been invited to SiteLedger. Submit these values to {baseUrl}/api/v1/invitations/accept along with your full name and password:\nTenantId: {current.TenantId}\nInvitationId: {invitation.Id}\nToken: {token}\nThis invitation expires in 48 hours.";
        try { await sender.SendAsync(address, "Your SiteLedger invitation", body, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Invitation email delivery failed: {ErrorType}", ex.GetType().Name);
            return Problem(statusCode: 503, title: "Invitation email delivery failed. Please retry.");
        }
        return Accepted();
    }

    [HttpPost("accept")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Accept(AcceptInvitation input, CancellationToken ct)
    {
        if (input.TenantId == Guid.Empty || input.InvitationId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token) || string.IsNullOrWhiteSpace(input.FullName)) return BadRequest();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Explicitly bind the administrative lookup and row lock to the tenant and invite IDs.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {input.TenantId}", ct);
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == input.TenantId && t.SubscriptionStatus == SubscriptionStatus.Active, ct);
        if (tenant is null) return BadRequest();
        var invitation = await db.Invitations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == input.TenantId && i.Id == input.InvitationId && !i.IsDeleted, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt <= DateTimeOffset.UtcNow || !AllowedRoles.Contains(invitation.Role, StringComparer.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(invitation.TokenHash), Encoding.ASCII.GetBytes(Hash(input.Token)))) return BadRequest();
        if (await users.FindByEmailAsync(invitation.Email) is not null) return BadRequest();
        var active = await db.Users.CountAsync(u => u.TenantId == input.TenantId && u.IsActive, ct);
        var viewers = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
            where u.TenantId == input.TenantId && u.IsActive && r.NormalizedName == "VIEWER" select u.Id).Distinct().CountAsync(ct);
        try { PlanRules.EnsureUserCapacity(tenant.Plan, invitation.Role == Roles.Viewer, active - viewers, viewers); }
        catch (PlanLimitException) { return Conflict(new { error = "The tenant has reached its user limit." }); }
        if (!await roles.RoleExistsAsync(invitation.Role) && !(await roles.CreateAsync(new IdentityRole(invitation.Role))).Succeeded) return Conflict();
        var user = new ApplicationUser { UserName = invitation.Email, Email = invitation.Email, EmailConfirmed = true, FullName = input.FullName.Trim(), TenantId = input.TenantId };
        var created = await users.CreateAsync(user, input.Password);
        if (!created.Succeeded) return BadRequest(new { errors = created.Errors.Select(e => e.Description) });
        if (!(await users.AddToRoleAsync(user, invitation.Role)).Succeeded) return Conflict();
        var now = DateTimeOffset.UtcNow;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Invitations` SET `AcceptedAt` = {now}, `RowVersion` = {Guid.NewGuid()} WHERE `TenantId` = {input.TenantId} AND `Id` = {input.InvitationId} AND `AcceptedAt` IS NULL AND `IsDeleted` = 0 AND `ExpiresAt` > {now}", ct);
        if (changed != 1) return BadRequest();
        var auditId = Guid.NewGuid();
        var auditVersion = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO `AuditLogs` (`Id`, `TenantId`, `CreatedAt`, `CreatedBy`, `IsDeleted`, `RowVersion`, `Action`, `EntityType`, `EntityId`, `UserId`, `Timestamp`, `Details`) VALUES ({auditId}, {input.TenantId}, {now}, {user.Id}, {false}, {auditVersion}, {"invitation.accept"}, {"Invitation"}, {input.InvitationId}, {user.Id}, {now}, {"{}"})", ct);
        await tx.CommitAsync(ct);
        return Ok(new { message = "Invitation accepted. You may sign in." });
    }
}
