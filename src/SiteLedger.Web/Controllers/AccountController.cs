using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using SiteLedger.Web.Services;
using SiteLedger.Web.Models;

namespace SiteLedger.Web.Controllers;

public sealed class AccountController(ApiClient api, WebSessions sessions) : Controller
{
    [HttpGet] public IActionResult Login() => View(new LoginForm());
    [HttpPost] public async Task<IActionResult> Login(LoginForm input)
    {
        if (!ModelState.IsValid) return LoginFailure(input);
        using var response = await api.Send(HttpMethod.Post, "api/v1/auth/login", new { input.Email, input.Password, input.AuthenticatorCode, input.RecoveryCode }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? "The service is unavailable. Try again later." : "Sign-in failed. Check your details and authenticator code."); return LoginFailure(input); }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = result.GetProperty("accessToken").GetString()!;
        var expires = result.GetProperty("expiresAt").GetDateTimeOffset();
        using var identityResponse = await api.Identity(token);
        if (!identityResponse.IsSuccessStatusCode) { ModelState.AddModelError("", "Your account could not be verified. Try again."); return LoginFailure(input); }
        var account = await identityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var key = "access:" + Guid.NewGuid().ToString("N");
        var absoluteExpiry = DateTimeOffset.UtcNow.AddDays(14);
        await sessions.Store(key, new WebSession(token, expires, result.GetProperty("refreshToken").GetString()!, absoluteExpiry));
        var claims = new List<Claim> { new(ClaimTypes.Name, input.Email), new("token_key", key) };
        if (account.TryGetProperty("userId", out var userId) && userId.GetString() is string id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        foreach (var role in account.GetProperty("roles").EnumerateArray())
            if (role.GetString() is string name) claims.Add(new Claim(ClaimTypes.Role, name));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = absoluteExpiry, IsPersistent = false });
        return RedirectToAction("Index", "Projects");
    }
    [HttpPost] public async Task<IActionResult> Logout()
    {
        var key = User.FindFirstValue("token_key");
        if (key is not null && !await api.Revoke(key)) TempData["Message"] = "Signed out locally. Remote session revocation could not be confirmed.";
        await HttpContext.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }
    [HttpGet] public IActionResult Register() => View(new RegistrationForm());
    [HttpPost] public async Task<IActionResult> Register(RegistrationForm input)
    {
        if (!ModelState.IsValid) return RegistrationFailure(input);
        using var response = await api.Send(HttpMethod.Post, "api/v1/account/register", new { input.Email, input.Password, input.FullName, input.CompanyName }, false);
        if (!response.IsSuccessStatusCode)
        {
            await AccountErrors(response, "Registration could not be completed. Please try again.");
            return RegistrationFailure(input);
        }
        TempData["Message"] = "If the account can be created, a confirmation email will be sent. Confirm it before signing in.";
        return RedirectToAction(nameof(Login));
    }
    [HttpGet] public IActionResult Recovery() => View(new RecoveryPage());
    [HttpPost] public async Task<IActionResult> Recovery([Bind(Prefix = "Request")] RecoveryRequestForm input)
    {
        if (!ModelState.IsValid) return RecoveryFailure(new RecoveryPage { Request = input });
        var path = input.ActionType == "confirm" ? "api/v1/account/confirmation/resend" : "api/v1/account/password/forgot";
        using var response = await api.Send(HttpMethod.Post, path, new { input.Email }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", "The request could not be sent. Try again later."); return RecoveryFailure(new RecoveryPage { Request = input }); }
        TempData["Message"] = "If the account is eligible, an email will be sent.";
        return RedirectToAction(nameof(Recovery));
    }
    [HttpPost] public async Task<IActionResult> Reset([Bind(Prefix = "Reset")] ResetPasswordForm input)
    {
        if (!ModelState.IsValid) return RecoveryFailure(new RecoveryPage { Reset = input });
        using var response = await api.Send(HttpMethod.Post, "api/v1/account/password/reset", new { input.Email, input.Token, input.Password }, false);
        if (!response.IsSuccessStatusCode)
        {
            await AccountErrors(response, "The reset could not be completed. Check the token and password.");
            return RecoveryFailure(new RecoveryPage { Reset = input });
        }
        TempData["Message"] = "Password reset. Sign in with your new password.";
        return RedirectToAction(nameof(Login));
    }
    [Authorize, HttpGet] public IActionResult Security() => View();
    [Authorize, HttpPost] public async Task<IActionResult> Security(string email, string password, string? code, string operation, string? recoveryCode)
    {
        if (operation is not ("setup" or "enable" or "disable" or "recovery-codes")) return BadRequest();
        var body = operation == "setup" ? (object)new { email, password } : new { email, password, code, recoveryCode };
        using var response = await api.Send(HttpMethod.Post, $"api/v1/auth/authenticator/{operation}", body, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", "Could not update authenticator settings. Check your password and code."); return View(); }
        if (operation == "recovery-codes")
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            ViewBag.RecoveryCodes = result.GetProperty("codes").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            Response.Headers.CacheControl = "no-store";
            return View();
        }
        if (operation == "setup")
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            ViewBag.Key = result.GetProperty("key").GetString();
            Response.Headers.CacheControl = "no-store";
            return View();
        }
        var key = User.FindFirstValue("token_key");
        if (key is not null && !await api.Revoke(key)) TempData["Message"] = "Signed out locally. Remote session revocation could not be confirmed.";
        await HttpContext.SignOutAsync();
        TempData["Message"] = "Authenticator settings updated. Sign in again.";
        return RedirectToAction(nameof(Login));
    }

    private void ForgetSecrets(params string[] names)
    {
        foreach (var name in names) ModelState.SetModelValue(name, (string?)null, null);
        Response.Headers.CacheControl = "no-store";
    }

    private ViewResult LoginFailure(LoginForm input)
    {
        input.Password = ""; input.AuthenticatorCode = null; input.RecoveryCode = null;
        ForgetSecrets(nameof(input.Password), nameof(input.AuthenticatorCode), nameof(input.RecoveryCode));
        var result = View(nameof(Login), input); result.StatusCode = 400; return result;
    }

    private ViewResult RegistrationFailure(RegistrationForm input)
    {
        input.Password = ""; input.ConfirmPassword = "";
        ForgetSecrets(nameof(input.Password), nameof(input.ConfirmPassword));
        var result = View(nameof(Register), input); result.StatusCode = 400; return result;
    }

    private ViewResult RecoveryFailure(RecoveryPage input)
    {
        input.Reset.Password = ""; input.Reset.ConfirmPassword = ""; input.Reset.Token = "";
        ForgetSecrets("Reset.Password", "Reset.ConfirmPassword", "Reset.Token");
        var result = View(nameof(Recovery), input); result.StatusCode = 400; return result;
    }

    private async Task AccountErrors(HttpResponseMessage response, string fallback)
    {
        // Only display bounded validation messages from our API, never its raw response body.
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var messages = errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()).Where(e => !string.IsNullOrWhiteSpace(e) && e.Length <= 256).Take(5).ToArray();
                    foreach (var message in messages) ModelState.AddModelError("", message!);
                    if (messages.Length > 0) return;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        }
        ModelState.AddModelError("", response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "Too many attempts. Wait a minute before trying again." : fallback);
    }
}
