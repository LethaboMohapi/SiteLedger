using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Api.Auth;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

public sealed record RegisterAccount([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(256)] string Password,
    [Required, StringLength(200)] string FullName, [Required, StringLength(200)] string CompanyName);
public sealed record EmailAccount([Required, EmailAddress, MaxLength(256)] string Email);
public sealed record ResetPasswordRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(4096)] string Token, [Required, MaxLength(256)] string Password);

[ApiController]
[Route("api/v1/account")]
[EnableRateLimiting("auth")]
public sealed class AccountController(SiteLedgerDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles,
    IAccountEmailSender email, IConfiguration config, ILogger<AccountController> logger) : ControllerBase
{
    private string? PublicBase => config["Account:PublicBaseUrl"]?.TrimEnd('/');
    private bool HasBase => Uri.TryCreate(PublicBase, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private static string Encode(string value) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    private static string? Decode(string value) { try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)); } catch (FormatException) { return null; } }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterAccount input, CancellationToken ct)
    {
        if (!HasBase || !email.IsConfigured) return Problem(statusCode: 503, title: "Account email is not configured.");
        if (string.IsNullOrWhiteSpace(input.FullName) || string.IsNullOrWhiteSpace(input.CompanyName)) return BadRequest();
        var address = input.Email.Trim();
        if (await users.FindByEmailAsync(address) is not null) return Accepted();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = new Tenant { CompanyName = input.CompanyName.Trim(), Slug = Guid.NewGuid().ToString("N"), Plan = Plan.Free, SubscriptionStatus = SubscriptionStatus.Active };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        if (!await roles.RoleExistsAsync(Roles.TenantAdmin))
        {
            var roleResult = await roles.CreateAsync(new IdentityRole(Roles.TenantAdmin));
            if (!roleResult.Succeeded) return Conflict(new { error = "Could not create account. Please retry." });
        }
        var user = new ApplicationUser { UserName = address, Email = address, FullName = input.FullName.Trim(), TenantId = tenant.Id };
        var result = await users.CreateAsync(user, input.Password);
        if (!result.Succeeded) return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        if (!(await users.AddToRoleAsync(user, Roles.TenantAdmin)).Succeeded) return Conflict(new { error = "Could not create account. Please retry." });
        try { await SendConfirmation(user, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Failed to queue confirmation email: {ErrorType}", ex.GetType().Name);
            return Problem(statusCode: 503, title: "Account registration is temporarily unavailable.");
        }
        await tx.CommitAsync(ct);
        return Accepted();
    }

    [HttpPost("confirmation/resend")]
    public async Task<IActionResult> Resend(EmailAccount input, CancellationToken ct)
    {
        if (!HasBase || !email.IsConfigured) return Problem(statusCode: 503, title: "Account email is not configured.");
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is not null && user.IsActive && !user.EmailConfirmed)
            try { await SendConfirmation(user, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError("Failed to queue confirmation email: {ErrorType}", ex.GetType().Name); }
        return Accepted();
    }

    [HttpGet("confirm")]
    public async Task<IActionResult> Confirm([FromQuery, Required, MaxLength(450)] string userId, [FromQuery, Required, MaxLength(4096)] string token)
    {
        var user = await users.FindByIdAsync(userId);
        var decoded = Decode(token);
        if (user is null || !user.IsActive || decoded is null) return BadRequest();
        return (await users.ConfirmEmailAsync(user, decoded)).Succeeded ? Ok(new { message = "Email confirmed. You may sign in." }) : BadRequest();
    }

    [HttpPost("password/forgot")]
    public async Task<IActionResult> Forgot(EmailAccount input, CancellationToken ct)
    {
        if (!HasBase || !email.IsConfigured) return Problem(statusCode: 503, title: "Account email is not configured.");
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is not null && user.IsActive && user.EmailConfirmed)
        {
            var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
            var text = $"Use this password reset token in the SiteLedger password reset screen or API: {token}";
            await TrySend(user.Email!, "SiteLedger password reset", text, ct);
        }
        return Accepted();
    }

    [HttpPost("password/reset")]
    public async Task<IActionResult> Reset(ResetPasswordRequest input)
    {
        var user = await users.FindByEmailAsync(input.Email.Trim());
        var token = Decode(input.Token);
        if (user is null || !user.IsActive || !user.EmailConfirmed || token is null) return BadRequest();
        var result = await users.ResetPasswordAsync(user, token, input.Password);
        return result.Succeeded ? Ok(new { message = "Password reset. Sign in with the new password." }) : BadRequest(new { errors = result.Errors.Select(e => e.Description) });
    }

    private async Task SendConfirmation(ApplicationUser user, CancellationToken ct)
    {
        var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var url = QueryHelpers.AddQueryString($"{PublicBase}/api/v1/account/confirm", new Dictionary<string, string?> { ["userId"] = user.Id, ["token"] = token });
        await email.SendAsync(user.Email!, "Confirm your SiteLedger account", $"Confirm your email by opening this link: {url}", ct);
    }

    private async Task TrySend(string recipient, string subject, string body, CancellationToken ct)
    {
        try { await email.SendAsync(recipient, subject, body, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError("Account email queueing failed: {ErrorType}", ex.GetType().Name); }
    }
}
