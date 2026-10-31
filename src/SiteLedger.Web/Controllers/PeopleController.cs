using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

namespace SiteLedger.Web.Controllers;

public sealed class PeopleController(ApiClient api) : Controller
{
    [Authorize, HttpGet] public async Task<IActionResult> Index()
    {
        using var response = await api.Send(HttpMethod.Get, "api/v1/users");
        if (!response.IsSuccessStatusCode) { TempData["Message"] = "Only tenant administrators can manage users."; return RedirectToAction("Index", "Projects"); }
        return View(await ApiClient.Array(response));
    }
    [Authorize, HttpPost] public async Task<IActionResult> Invite(string email, string role)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/invitations", new { email, role });
        TempData["Message"] = response.IsSuccessStatusCode ? "Invitation sent." : "Invitation could not be sent. Check the email, permissions and email configuration.";
        return RedirectToAction(nameof(Index));
    }
    [Authorize, HttpPost] public async Task<IActionResult> Update(string id, string fullName, string role, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 450) return BadRequest();
        using var response = await api.Send(HttpMethod.Put, "api/v1/users/" + Uri.EscapeDataString(id), new { fullName, role, isActive });
        TempData["Message"] = response.IsSuccessStatusCode ? "User updated." : "User could not be updated. Check plan limits and the active administrator requirement.";
        return RedirectToAction(nameof(Index));
    }
    [HttpGet] public IActionResult Accept() => View();
    [HttpPost] public async Task<IActionResult> Accept(Guid tenantId, Guid invitationId, string token, string fullName, string password)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/invitations/accept", new { tenantId, invitationId, token, fullName, password }, false);
        if (!response.IsSuccessStatusCode) { ModelState.AddModelError("", "The invitation could not be accepted. Check the details, expiry and password."); return View(); }
        TempData["Message"] = "Invitation accepted. You can now sign in.";
        return RedirectToAction("Login", "Account");
    }
}
