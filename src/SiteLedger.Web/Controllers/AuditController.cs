using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

namespace SiteLedger.Web.Controllers;

[Authorize(Roles = "TenantAdmin")]
public sealed class AuditController(ApiClient api) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1)
    {
        page = Math.Clamp(page, 1, 100000);
        using var response = await api.Send(HttpMethod.Get, $"api/v1/audit?page={page}&pageSize=50");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return RedirectToAction("Login", "Account");
        if (!response.IsSuccessStatusCode) { ViewBag.Error = "Audit events could not be loaded."; return View(Array.Empty<System.Text.Json.JsonElement>()); }
        ViewBag.Page = page;
        Response.Headers.CacheControl = "no-store";
        return View(await ApiClient.Array(response));
    }
}
