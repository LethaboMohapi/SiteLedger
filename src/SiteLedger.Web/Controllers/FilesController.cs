using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SiteLedger.Web.Services;

namespace SiteLedger.Web.Controllers;

[Authorize]
public sealed class FilesController(ApiClient api) : Controller
{
    [HttpGet] public async Task<IActionResult> Index(Guid id)
    {
        using var response = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/files");
        if (!response.IsSuccessStatusCode) { TempData["Message"] = "Files could not be loaded."; return RedirectToAction("Detail", "Projects", new { id }); }
        ViewBag.ProjectId = id;
        return View(await ApiClient.Array(response));
    }
    [HttpPost, RequestSizeLimit(106954752)]
    public async Task<IActionResult> Upload(Guid id, IFormFile file, int category)
    {
        if (file is null || file.Length <= 0 || file.Length > 104857600) { TempData["Message"] = "Choose a file within the permitted size."; return RedirectToAction(nameof(Index), new { id }); }
        using var response = await api.Upload($"api/v1/projects/{id}/files", file, category);
        TempData["Message"] = response.IsSuccessStatusCode ? "File uploaded." : "File could not be uploaded. Check your role, file type and plan limits.";
        return RedirectToAction(nameof(Index), new { id });
    }
    [HttpGet] public async Task<IActionResult> Download(Guid id, Guid fileId)
    {
        using var response = await api.Send(HttpMethod.Get, $"api/v1/projects/{id}/files/{fileId}");
        if (!response.IsSuccessStatusCode) return NotFound();
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "download";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(await response.Content.ReadAsByteArrayAsync(), "application/octet-stream", name);
    }
    [HttpPost] public async Task<IActionResult> Share(Guid id, Guid fileId, bool shared)
    {
        using var response = await api.Send(HttpMethod.Put, $"api/v1/projects/{id}/files/{fileId}/sharing", shared);
        TempData["Message"] = response.IsSuccessStatusCode ? "File sharing updated." : "File sharing could not be changed.";
        return RedirectToAction(nameof(Index), new { id });
    }
    [HttpPost] public async Task<IActionResult> Delete(Guid id, Guid fileId)
    {
        using var response = await api.Send(HttpMethod.Delete, $"api/v1/projects/{id}/files/{fileId}");
        TempData["Message"] = response.IsSuccessStatusCode ? "File deleted." : "File could not be deleted.";
        return RedirectToAction(nameof(Index), new { id });
    }
}
