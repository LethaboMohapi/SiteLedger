using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/files")]
[Authorize(Policy = Permissions.ReadProjects)]
public sealed class ProjectFilesController(SiteLedgerDbContext db, ICurrentTenant current, IConfiguration config, IWebHostEnvironment env, ILogger<ProjectFilesController> logger) : ControllerBase
{
    private string? Root()
    {
        var root = config["Storage:Path"];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return null;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        var webRoot = Path.GetFullPath(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.Equals(webRoot, comparison) || root.StartsWith(webRoot + Path.DirectorySeparatorChar, comparison)) return null;
        return root;
    }
    private string PathFor(string root, Guid tenantId, Guid projectId, Guid fileId) =>
        Path.Combine(root, tenantId.ToString("N"), projectId.ToString("N"), fileId.ToString("N"));
    private async Task<bool> CanAccess(Guid projectId, CancellationToken ct) => await db.Projects.AnyAsync(p => p.Id == projectId, ct) &&
        (User.IsInRole(Roles.TenantAdmin) || await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId, ct));
    private static object ViewFile(FileAsset f) => new { f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Sha256, f.Category, f.UploadedBy, f.IsSharedWithClient, f.CreatedAt };
    private static string SafeName(string supplied, string suffix)
    {
        var name = supplied.Replace('\\', '/').Split('/').LastOrDefault() ?? "file";
        name = new string(name.Where(ch => !char.IsControl(ch) && ch is not ('/' or '\\' or ':' or '"')).ToArray()).Trim().Trim('.');
        name = Path.GetFileNameWithoutExtension(name).Trim().Trim('.');
        if (name.Length > 140) name = name[..140];
        return (string.IsNullOrWhiteSpace(name) ? "file" : name) + suffix;
    }
    private static (string Type, string Extension)? Detect(byte[] head, int length)
    {
        if (length >= 5 && head.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return ("application/pdf", ".pdf");
        if (length >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ("image/png", ".png");
        if (length >= 3 && head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) return ("image/jpeg", ".jpg");
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var query = db.FileAssets.AsNoTracking().Where(f => f.ProjectId == projectId);
        if (User.IsInRole(Roles.Viewer)) query = query.Where(f => f.IsSharedWithClient);
        return Ok((await query.OrderByDescending(f => f.CreatedAt).Take(100).ToListAsync(ct)).Select(ViewFile));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.UploadFiles)]
    [RequestSizeLimit(106954752)]
    public async Task<IActionResult> Upload(Guid projectId, IFormFile file, [FromForm] FileCategory category, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var root = Root();
        if (root is null) return Problem(statusCode: 503, title: "Private storage is not configured.");
        if (file is null || file.Length <= 0 || !Enum.IsDefined(category)) return BadRequest();
        var tenantId = current.TenantId!.Value;
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        try { PlanRules.EnsureUploadCapacity(tenant.Plan, tenant.StorageUsedBytes, file.Length); }
        catch (PlanLimitException ex) { return Conflict(new { error = ex.Message }); }
        var id = Guid.NewGuid();
        var path = PathFor(root, tenantId, projectId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long total = 0;
        var header = new byte[8]; var headerLength = 0;
        string digest;
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using var input = file.OpenReadStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; int n;
            while ((n = await input.ReadAsync(buffer, ct)) != 0)
            {
                total += n;
                if (total > file.Length || total > PlanLimits.For(tenant.Plan).MaxFileBytes) throw new InvalidDataException("File exceeded the declared size.");
                var copy = Math.Min(n, header.Length - headerLength);
                if (copy > 0) { Buffer.BlockCopy(buffer, 0, header, headerLength, copy); headerLength += copy; }
                hash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            if (total != file.Length) throw new InvalidDataException("File size did not match.");
            digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (InvalidDataException) { System.IO.File.Delete(path); return BadRequest(); }
        catch { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); throw; }
        var type = Detect(header, headerLength);
        if (type is null) { System.IO.File.Delete(path); return BadRequest(new { error = "Only PDF, PNG and JPEG files are supported." }); }
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", ct);
            if (!await CanAccess(projectId, ct)) { System.IO.File.Delete(path); return NotFound(); }
            tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            PlanRules.EnsureUploadCapacity(tenant.Plan, tenant.StorageUsedBytes, total);
            var asset = new FileAsset { Id = id, ProjectId = projectId, FileName = SafeName(file.FileName, type.Value.Extension), ContentType = type.Value.Type,
                BlobPath = id.ToString("N"), SizeBytes = total, Sha256 = digest, Category = category, UploadedBy = current.UserId! };
            tenant.StorageUsedBytes += total;
            db.FileAssets.Add(asset);
            db.AuditLogs.Add(new AuditLog { Action = "file.upload", EntityType = "FileAsset", EntityId = id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return StatusCode(201, ViewFile(asset));
        }
        catch (PlanLimitException ex) { System.IO.File.Delete(path); return Conflict(new { error = ex.Message }); }
        catch { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); throw; }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid projectId, Guid id, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var file = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Id == id, ct);
        if (file is null || (User.IsInRole(Roles.Viewer) && !file.IsSharedWithClient)) return NotFound();
        var root = Root();
        if (root is null) return Problem(statusCode: 503, title: "Private storage is not configured.");
        var path = PathFor(root, current.TenantId!.Value, projectId, id);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous), "application/octet-stream", file.FileName);
    }

    [HttpPut("{id:guid}/sharing")]
    [Authorize(Policy = Permissions.ShareFiles)]
    public async Task<IActionResult> Share(Guid projectId, Guid id, [FromBody] bool shared, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var file = await db.FileAssets.SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Id == id, ct);
        if (file is null) return NotFound();
        file.IsSharedWithClient = shared;
        db.AuditLogs.Add(new AuditLog { Action = shared ? "file.share" : "file.unshare", EntityType = "FileAsset", EntityId = id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
        try { await db.SaveChangesAsync(ct); return Ok(ViewFile(file)); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.UploadFiles)]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        if (!await CanAccess(projectId, ct)) return NotFound();
        var root = Root();
        if (root is null) return Problem(statusCode: 503, title: "Private storage is not configured.");
        var tenantId = current.TenantId!.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", ct);
        var file = await db.FileAssets.SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Id == id, ct);
        if (file is null) return NotFound();
        if (file.UploadedBy != current.UserId && !User.IsInRole(Roles.TenantAdmin) && !User.IsInRole(Roles.Manager)) return Forbid();
        var path = PathFor(root, tenantId, projectId, id);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        var moved = false;
        try { if (System.IO.File.Exists(path)) { System.IO.File.Move(path, pending); moved = true; } }
        catch (IOException ex) { logger.LogWarning(ex, "Private file removal failed for {FileId}", id); return Problem(statusCode: 503, title: "File removal failed."); }
        try
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            tenant.StorageUsedBytes = Math.Max(0, tenant.StorageUsedBytes - file.SizeBytes);
            db.FileAssets.Remove(file);
            db.AuditLogs.Add(new AuditLog { Action = "file.delete", EntityType = "FileAsset", EntityId = id, UserId = current.UserId!, Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            if (moved) System.IO.File.Move(pending, path);
            if (ex is DbUpdateConcurrencyException) return Conflict();
            throw;
        }
        if (moved)
        {
            try { System.IO.File.Delete(pending); }
            catch (IOException ex) { logger.LogWarning(ex, "Private file cleanup remains pending for {FileId}", id); }
        }
        return NoContent();
    }
}
