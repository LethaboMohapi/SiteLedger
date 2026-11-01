using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Api.Controllers;
using SiteLedger.Core;
using SiteLedger.Infrastructure;
using Testcontainers.MySql;

namespace SiteLedger.Tests;

public sealed class ManagementTests
{
    private sealed record Tenant(Guid? TenantId, string? UserId) : ICurrentTenant;
    private static ControllerContext Context(string role) => new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test")) } };

    [Fact]
    public async Task TaskAssignmentAndSoftDeletionRespectProjectTenantAndVersion()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>().UseMySql(mysql.GetConnectionString(), new MySqlServerVersion(new Version(8, 0, 36))).Options;
        var tenantA = Guid.NewGuid(); var tenantB = Guid.NewGuid();
        var current = new Tenant(tenantA, "manager");
        await using var db = new SiteLedgerDbContext(options, current);
        await db.Database.MigrateAsync();
        db.Tenants.AddRange(new SiteLedger.Core.Tenant { Id = tenantA, CompanyName = "A", Slug = "a" }, new SiteLedger.Core.Tenant { Id = tenantB, CompanyName = "B", Slug = "b" });
        db.Users.AddRange(new ApplicationUser { Id = "manager", UserName = "manager", NormalizedUserName = "MANAGER", TenantId = tenantA, EmailConfirmed = true },
            new ApplicationUser { Id = "member", UserName = "member", NormalizedUserName = "MEMBER", TenantId = tenantA, EmailConfirmed = true },
            new ApplicationUser { Id = "other", UserName = "other", NormalizedUserName = "OTHER", TenantId = tenantB, EmailConfirmed = true });
        var project = new Project { Name = "Site", ClientName = "Client", SiteAddress = "Site", StartDate = new DateOnly(2026, 1, 1) };
        var task = new ProjectTask { ProjectId = project.Id, Title = "Task" };
        db.Projects.Add(project); db.ProjectTasks.Add(task);
        db.ProjectMembers.AddRange(new ProjectMember { ProjectId = project.Id, UserId = "manager" }, new ProjectMember { ProjectId = project.Id, UserId = "member" });
        var expense = new Expense { ProjectId = project.Id, CreatedByUserId = "manager", Supplier = "S", Amount = 1, ExpenseDate = new DateOnly(2026, 1, 1) };
        db.Expenses.Add(expense); await db.SaveChangesAsync();
        var taskController = new ProjectTasksController(db, current) { ControllerContext = Context(Roles.Manager) };
        var listResult = Assert.IsType<OkObjectResult>(await taskController.Assignees(project.Id, default));
        var json = System.Text.Json.JsonSerializer.Serialize(listResult.Value);
        Assert.Contains("member", json);
        Assert.DoesNotContain("other", json);
        var controller = new ProjectManagementController(db, current) { ControllerContext = Context(Roles.Manager) };
        Assert.IsType<NotFoundResult>(await controller.Assign(project.Id, task.Id, new AssignTaskRequest("other", task.RowVersion), default));
        var original = task.RowVersion;
        Assert.IsType<OkObjectResult>(await controller.Assign(project.Id, task.Id, new AssignTaskRequest("member", original), default));
        Assert.IsType<ConflictObjectResult>(await controller.DeleteTask(project.Id, task.Id, original, default));
        Assert.IsType<NoContentResult>(await controller.DeleteTask(project.Id, task.Id, task.RowVersion, default));
        Assert.Empty(await db.ProjectTasks.ToListAsync());
        Assert.IsType<ConflictObjectResult>(await controller.DeleteProject(project.Id, project.RowVersion, default));
        var deleteRecords = new RecordDeletionController(db, current) { ControllerContext = Context(Roles.Manager) };
        Assert.IsType<NoContentResult>(await deleteRecords.Delete(project.Id, "expenses", expense.Id, expense.RowVersion, default));
        Assert.IsType<NoContentResult>(await controller.DeleteProject(project.Id, project.RowVersion, default));
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "task.delete"));
    }
}
