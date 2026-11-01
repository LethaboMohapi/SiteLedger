using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using SiteLedger.Api.Auth;

namespace SiteLedger.Api.Controllers;

public sealed record LoginRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(256)] string Password, [MaxLength(20)] string? AuthenticatorCode = null, [MaxLength(64)] string? RecoveryCode = null);

public sealed record AuthenticatorRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(256)] string Password, [MaxLength(20)] string? Code = null, [MaxLength(64)] string? RecoveryCode = null);

public sealed record RefreshRequest([Required, MaxLength(160)] string RefreshToken);

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(ILoginService login, AuthenticatorService authenticator, RefreshTokenService refresh) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await login.LoginAsync(request.Email, request.Password, request.AuthenticatorCode, request.RecoveryCode, cancellationToken);
        return result is null ? Unauthorized() : Ok(result);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await refresh.RotateAsync(request.RefreshToken, ct);
        Response.Headers.CacheControl = "no-store";
        return result is null ? Unauthorized() : Ok(result);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await refresh.RevokeAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpPost("authenticator/setup")]
    public async Task<IActionResult> Setup(LoginRequest request, CancellationToken ct)
    {
        var key = await authenticator.Setup(request.Email, request.Password, ct);
        return key is null ? Unauthorized() : Ok(new { key });
    }

    [HttpPost("authenticator/enable")]
    public async Task<IActionResult> Enable(AuthenticatorRequest request, CancellationToken ct) =>
        await authenticator.SetEnabled(request.Email, request.Password, request.Code ?? "", true, ct) ? NoContent() : Unauthorized();

    [HttpPost("authenticator/disable")]
    public async Task<IActionResult> Disable(AuthenticatorRequest request, CancellationToken ct) =>
        await authenticator.SetEnabled(request.Email, request.Password, request.Code ?? "", false, ct, request.RecoveryCode) ? NoContent() : Unauthorized();

    [HttpPost("authenticator/recovery-codes")]
    public async Task<IActionResult> RecoveryCodes(AuthenticatorRequest request, CancellationToken ct)
    {
        var codes = await authenticator.GenerateRecoveryCodes(request.Email, request.Password, request.Code ?? "", ct);
        if (codes is null) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        return Ok(new { codes });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new { name = User.Identity?.Name, userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray() });
}
