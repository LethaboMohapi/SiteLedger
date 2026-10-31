using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

namespace SiteLedger.Web.Controllers;

[Authorize]
public sealed class ProjectsController(ApiClient api) : Controller
{
    [HttpGet] public async Task<IActionResult> Index(int page = 1)
    {
        page = Math.Clamp(page, 1, 100000);
        using var response = await api.Send(HttpMethod.Get, $"api/v1/projects?page={page}&pageSize=20");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return RedirectToAction("Login", "Account");
        if (!response.IsSuccessStatusCode) { ViewBag.Error = "Projects could not be loaded."; return View(Array.Empty<System.Text.Json.JsonElement>()); }
        ViewBag.Page = page;
        using var dashboard = await api.Send(HttpMethod.Get, "api/v1/dashboard");
        if (dashboard.IsSuccessStatusCode) ViewBag.Dashboard = await dashboard.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return View(await ApiClient.Array(response));
    }
    [HttpPost] public async Task<IActionResult> Create(string name, string clientName, string siteAddress, decimal budget, string startDate, string? endDate, int status)
    {
        using var response = await api.Send(HttpMethod.Post, "api/v1/projects", new { name, clientName, siteAddress, budget, startDate, endDate = string.IsNullOrEmpty(endDate) ? null : endDate, status });
        TempData["Message"] = response.IsSuccessStatusCode ? "Project created." : "Project could not be created. Check permissions, the plan limit and your details.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost] public async Task<IActionResult> Update(Guid id, Guid version, string name, string clientName, string siteAddress, decimal budget, string startDate, string? endDate, int status)
    {
        using var response = await api.Send(HttpMethod.Put, $"api/v1/projects/{id}?version={version}", new { name, clientName, siteAddress, budget, startDate, endDate = string.IsNullOrEmpty(endDate) ? null : endDate, status });
        TempData["Message"] = response.IsSuccessStatusCode ? "Project updated." : "Project could not be updated. Refresh and check permissions and plan limits.";
        return RedirectToAction(nameof(Index));
    }
    [HttpGet] public async Task<IActionResult> Detail(Guid id)
    {
        using var tasks = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/tasks");
        if (tasks.StatusCode == System.Net.HttpStatusCode.Unauthorized) return RedirectToAction("Login", "Account");
        if (!tasks.IsSuccessStatusCode) return NotFound();
        using var reports = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/reports");
        using var expenses = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/expenses");
        if (!reports.IsSuccessStatusCode || !expenses.IsSuccessStatusCode) { TempData["Message"] = "Project records could not be loaded."; return RedirectToAction(nameof(Index)); }
        ViewBag.ProjectId = id;
        ViewBag.Reports = await ApiClient.Array(reports);
        ViewBag.Expenses = await ApiClient.Array(expenses);
        return View(await ApiClient.Array(tasks));
    }
    [HttpPost] public async Task<IActionResult> Task(Guid id, string title, string? description, string? dueDate, int priority, int status, int percentComplete, Guid? itemId, Guid? version)
    {
        var path = $"api/v1/projects/{id}/tasks" + (itemId.HasValue ? $"/{itemId}?version={version}" : "");
        using var result = await api.Send(itemId.HasValue ? HttpMethod.Put : HttpMethod.Post, path, new { title, description = description ?? "", dueDate = string.IsNullOrEmpty(dueDate) ? null : dueDate, priority, status, percentComplete });
        TempData["Message"] = result.IsSuccessStatusCode ? "Task saved." : "Task could not be saved. Refresh and check your access and values.";
        return RedirectToAction(nameof(Detail), new { id });
    }
    [HttpPost] public async Task<IActionResult> Report(Guid id, string reportDate, string weather, int workersOnSite, string workSummary, string? issues, Guid? itemId, Guid? version)
    {
        var path = $"api/v1/projects/{id}/reports" + (itemId.HasValue ? $"/{itemId}?version={version}" : "");
        using var result = await api.Send(itemId.HasValue ? HttpMethod.Put : HttpMethod.Post, path, new { reportDate, weather, workersOnSite, workSummary, issues = issues ?? "" });
        TempData["Message"] = result.IsSuccessStatusCode ? "Report saved." : "Report could not be saved. Only its author can edit a draft or rejected report.";
        return RedirectToAction(nameof(Detail), new { id });
    }
    [HttpPost] public async Task<IActionResult> Expense(Guid id, string expenseDate, int category, decimal amount, string supplier, Guid? itemId, Guid? version)
    {
        var path = $"api/v1/projects/{id}/expenses" + (itemId.HasValue ? $"/{itemId}?version={version}" : "");
        using var result = await api.Send(itemId.HasValue ? HttpMethod.Put : HttpMethod.Post, path, new { expenseDate, category, amount, supplier });
        TempData["Message"] = result.IsSuccessStatusCode ? "Expense saved." : "Expense could not be saved. Only its author can edit a draft or rejected expense.";
        return RedirectToAction(nameof(Detail), new { id });
    }
    [HttpPost] public async Task<IActionResult> Transition(Guid id, Guid itemId, string kind, string actionType, string? comment, bool approve = false)
    {
        if (kind is not ("reports" or "expenses") || actionType is not ("submit" or "review")) return BadRequest();
        using var response = await api.Send(HttpMethod.Post, $"api/v1/projects/{id}/{kind}/{itemId}/{actionType}", actionType == "review" ? new { approve, comment } : null);
        TempData["Message"] = response.IsSuccessStatusCode ? "Record updated." : "Action not permitted or record is no longer in the required state.";
        return RedirectToAction(nameof(Detail), new { id });
    }
    [HttpGet] public async Task<IActionResult> Export(Guid? projectId, string? kind)
    {
        if (projectId.HasValue && kind is not ("reports" or "expenses")) return BadRequest();
        var path = projectId.HasValue ? $"api/v1/exports/projects/{projectId}/{kind}.csv" : "api/v1/exports/projects.csv";
        using var response = await api.Send(HttpMethod.Get, path);
        if (!response.IsSuccessStatusCode) { TempData["Message"] = "Export requires an eligible plan and role."; return RedirectToAction(nameof(Index)); }
        return File(await response.Content.ReadAsByteArrayAsync(), "text/csv; charset=utf-8", $"siteledger-{kind ?? "projects"}.csv");
    }
    [HttpGet] public async Task<IActionResult> Members(Guid id)
    {
        using var members = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/members");
        using var users = await api.Send(HttpMethod.Get, "api/v1/users");
        if (!members.IsSuccessStatusCode || !users.IsSuccessStatusCode) { TempData["Message"] = "Only tenant administrators can manage members."; return RedirectToAction(nameof(Detail), new { id }); }
        ViewBag.ProjectId = id; ViewBag.Users = await ApiClient.Array(users);
        return View(await ApiClient.Array(members));
    }
    [HttpPost] public async Task<IActionResult> Member(Guid id, string userId, string? roleOnProject, bool remove = false)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450) return BadRequest();
        var path = $"api/v1/projects/{id}/members" + (remove ? "/" + Uri.EscapeDataString(userId) : "");
        using var response = await api.Send(remove ? HttpMethod.Delete : HttpMethod.Put, path, remove ? null : new { userId, roleOnProject });
        TempData["Message"] = response.IsSuccessStatusCode ? "Membership updated." : "Membership could not be updated.";
        return RedirectToAction(nameof(Members), new { id });
    }

}
