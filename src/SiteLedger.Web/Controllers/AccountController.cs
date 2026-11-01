using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using SiteLedger.Web.Services;

namespace SiteLedger.Web.Controllers;

public sealed class AccountController(ApiClient api, IDistributedCache cache) : Controller
{
    [HttpGet] public IActionResult Login() => View();
    [HttpPost] public async Task<IActionResult> Login(string email, string password, string? authenticatorCode, string? recoveryCode)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/auth/login", new { email, password, authenticatorCode, recoveryCode }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? "The service is unavailable. Try again later." : "Sign-in failed. Check your details and authenticator code."); return View(); }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = result.GetProperty("accessToken").GetString()!;
        var expires = result.GetProperty("expiresAt").GetDateTimeOffset();
        using var identityResponse = await api.Identity(token);
        if (!identityResponse.IsSuccessStatusCode) { ModelState.AddModelError("", "Your account could not be verified. Try again."); return View(); }
        var account = await identityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var key = "access:" + Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(key, token, new DistributedCacheEntryOptions { AbsoluteExpiration = expires });
        var claims = new List<Claim> { new(ClaimTypes.Name, email), new("token_key", key) };
        if (account.TryGetProperty("userId", out var userId) && userId.GetString() is string id) claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        foreach (var role in account.GetProperty("roles").EnumerateArray())
            if (role.GetString() is string name) claims.Add(new Claim(ClaimTypes.Role, name));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = expires, IsPersistent = false });
        return RedirectToAction("Index", "Projects");
    }
    [HttpPost] public async Task<IActionResult> Logout()
    {
        var key = User.FindFirstValue("token_key");
        if (key is not null) await cache.RemoveAsync(key);
        await HttpContext.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }
    [HttpGet] public IActionResult Register() => View();
    [HttpPost] public async Task<IActionResult> Register(string email, string password, string fullName, string companyName)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/account/register", new { email, password, fullName, companyName }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", "Registration could not be completed. Check your details and password requirements, then try again."); return View(); }
        TempData["Message"] = "If the account can be created, a confirmation email will be sent. Confirm it before signing in.";
        return RedirectToAction(nameof(Login));
    }
    [HttpGet] public IActionResult Recovery() => View();
    [HttpPost] public async Task<IActionResult> Recovery(string email, string actionType)
    {
        var path = actionType == "confirm" ? "api/v1/account/confirmation/resend" : "api/v1/account/password/forgot";
        using var response = await api.Send(HttpMethod.Post, path, new { email }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", "The request could not be sent. Try again later."); return View(); }
        TempData["Message"] = "If the account is eligible, an email will be sent.";
        return RedirectToAction(nameof(Recovery));
    }
    [HttpPost] public async Task<IActionResult> Reset(string email, string token, string password)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/account/password/reset", new { email, token, password }, false);
        TempData["Message"] = response.IsSuccessStatusCode ? "Password reset. Sign in with your new password." : "The reset could not be completed. Check the token and password.";
        return RedirectToAction(response.IsSuccessStatusCode ? nameof(Login) : nameof(Recovery));
    }
    [Authorize, HttpGet] public IActionResult Security() => View();
    [Authorize, HttpPost] public async Task<IActionResult> Security(string email, string password, string? code, string operation)
    {
        if (operation is not ("setup" or "enable" or "disable" or "recovery-codes")) return BadRequest();
        var body = operation == "setup" ? (object)new { email, password } : new { email, password, code };
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
        if (key is not null) await cache.RemoveAsync(key);
        await HttpContext.SignOutAsync();
        TempData["Message"] = "Authenticator settings updated. Sign in again.";
        return RedirectToAction(nameof(Login));
    }

}
