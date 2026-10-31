using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using SiteLedger.Api.Auth;

namespace SiteLedger.Api.Controllers;

public sealed record LoginRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(256)] string Password, [MaxLength(20)] string? AuthenticatorCode = null);

public sealed record AuthenticatorRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(256)] string Password, [Required, MaxLength(20)] string Code);

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(ILoginService login, AuthenticatorService authenticator) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await login.LoginAsync(request.Email, request.Password, request.AuthenticatorCode, cancellationToken);
        return result is null ? Unauthorized() : Ok(result);
    }

    [HttpPost("authenticator/setup")]
    public async Task<IActionResult> Setup(LoginRequest request, CancellationToken ct)
    {
        var key = await authenticator.Setup(request.Email, request.Password, ct);
        return key is null ? Unauthorized() : Ok(new { key });
    }

    [HttpPost("authenticator/enable")]
    public async Task<IActionResult> Enable(AuthenticatorRequest request, CancellationToken ct) =>
        await authenticator.SetEnabled(request.Email, request.Password, request.Code, true, ct) ? NoContent() : Unauthorized();

    [HttpPost("authenticator/disable")]
    public async Task<IActionResult> Disable(AuthenticatorRequest request, CancellationToken ct) =>
        await authenticator.SetEnabled(request.Email, request.Password, request.Code, false, ct) ? NoContent() : Unauthorized();

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new { name = User.Identity?.Name });
}
