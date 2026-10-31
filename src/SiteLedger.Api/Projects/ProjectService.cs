using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Projects;

public sealed record ProjectView(Guid Id, string Name, string ClientName, string SiteAddress, decimal Budget,
    DateOnly StartDate, DateOnly? EndDate, ProjectStatus Status, Guid RowVersion);
public sealed record NewProject(string Name, string ClientName, string SiteAddress, decimal Budget, DateOnly StartDate, DateOnly? EndDate, ProjectStatus Status);

public interface IProjectService
{
    Task<IReadOnlyList<ProjectView>> ListAsync(bool allProjects, int page, int pageSize, CancellationToken cancellationToken);
    Task<ProjectView> CreateAsync(NewProject request, CancellationToken cancellationToken);
}

public sealed class ProjectService(SiteLedgerDbContext db, ICurrentTenant current) : IProjectService
{
    public async Task<IReadOnlyList<ProjectView>> ListAsync(bool allProjects, int page, int pageSize, CancellationToken cancellationToken)
    {
        var userId = current.UserId ?? throw new UnauthorizedAccessException();
        var query = db.Projects.AsNoTracking();
        if (!allProjects)
            query = query.Where(project => db.ProjectMembers.Any(member => member.ProjectId == project.Id && member.UserId == userId));
        return await query.OrderBy(project => project.Name).ThenBy(project => project.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(project => new ProjectView(project.Id, project.Name, project.ClientName, project.SiteAddress,
                project.Budget, project.StartDate, project.EndDate, project.Status, project.RowVersion))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectView> CreateAsync(NewProject request, CancellationToken cancellationToken)
    {
        var tenantId = current.TenantId ?? throw new UnauthorizedAccessException();
        var userId = current.UserId ?? throw new UnauthorizedAccessException();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize quota changes on the tenant row across concurrent requests.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {tenantId}", cancellationToken);
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        if (request.Status == ProjectStatus.Active)
            PlanRules.EnsureProjectCapacity(tenant.Plan, await db.Projects.CountAsync(p => p.Status == ProjectStatus.Active, cancellationToken));
        var project = new Project { Name = request.Name.Trim(), ClientName = request.ClientName.Trim(), SiteAddress = request.SiteAddress.Trim(),
            Budget = request.Budget, StartDate = request.StartDate, EndDate = request.EndDate, Status = request.Status };
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = userId, RoleOnProject = "Creator" });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ProjectView(project.Id, project.Name, project.ClientName, project.SiteAddress, project.Budget,
            project.StartDate, project.EndDate, project.Status, project.RowVersion);
    }
}
