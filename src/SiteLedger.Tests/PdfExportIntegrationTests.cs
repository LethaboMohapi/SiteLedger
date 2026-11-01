using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PdfSharp.Pdf.IO;
using SiteLedger.Api.Auth;
using SiteLedger.Api.Exports;
using SiteLedger.Core;
using SiteLedger.Infrastructure;
using Testcontainers.MySql;

namespace SiteLedger.Tests;

public sealed class PdfExportIntegrationTests
{
    private sealed record TenantContext(Guid? TenantId, string? UserId) : ICurrentTenant;

    [Fact]
    public void ReportRendererPaginatesLongTextAndEmbedsFont()
    {
        var pdf = PdfReportRenderer.Render(new Project { Name = "Roadworks", ClientName = "Client", SiteAddress = "Site" },
            new SiteReport { ReportDate = new DateOnly(2026, 1, 1), Weather = "Clear", WorkSummary = string.Join(" ", Enumerable.Repeat("Long section", 1600)), Issues = "None" });
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.ReadOnly);
        Assert.True(document.PageCount > 1);
        Assert.Contains("Roboto", System.Text.Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public async Task PdfExportRequiresPlanRoleAndProjectAccess()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var values = new Dictionary<string, string?> {
            ["ConnectionStrings__SiteLedger"] = mysql.GetConnectionString(), ["Authentication__Jwt__Issuer"] = "siteledger-test",
            ["Authentication__Jwt__Audience"] = "siteledger-test-client", ["Authentication__Jwt__SigningKey"] = "test-key-used-only-for-integration-test-123456" };
        var originals = values.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var p in values) Environment.SetEnvironmentVariable(p.Key, p.Value);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var manager = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var outsider = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            const string password = "Valid-test-password-123!";
            Guid tenantId, projectId, reportId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                await db.Database.MigrateAsync();
                var a = new Tenant { CompanyName = "A", Slug = "pdf-a", Plan = Plan.Free };
                var b = new Tenant { CompanyName = "B", Slug = "pdf-b", Plan = Plan.Pro };
                db.Tenants.AddRange(a, b); await db.SaveChangesAsync(); tenantId = a.Id;
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var role in new[] { Roles.TenantAdmin, Roles.Manager }) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                async Task CreateUser(string email, Guid tenant, string role)
                {
                    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = email, TenantId = tenant };
                    Assert.True((await users.CreateAsync(user, password)).Succeeded);
                    Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                }
                await CreateUser("pdf-admin@example.test", a.Id, Roles.TenantAdmin);
                await CreateUser("pdf-manager@example.test", a.Id, Roles.Manager);
                await CreateUser("pdf-other@example.test", b.Id, Roles.TenantAdmin);
            }
            var options = new DbContextOptionsBuilder<SiteLedgerDbContext>().UseMySql(mysql.GetConnectionString(), new MySqlServerVersion(new Version(8, 0, 36))).Options;
            await using (var seed = new SiteLedgerDbContext(options, new TenantContext(tenantId, "seed")))
            {
                var project = new Project { Name = "Site", ClientName = "Client", SiteAddress = "Address", StartDate = new DateOnly(2026, 1, 1) };
                var report = new SiteReport { ProjectId = project.Id, CreatedByUserId = "seed", ReportDate = new DateOnly(2026, 1, 1), Weather = "Clear", WorkSummary = "Concrete poured", Issues = "None" };
                seed.Projects.Add(project); seed.SiteReports.Add(report); await seed.SaveChangesAsync();
                projectId = project.Id; reportId = report.Id;
            }
            async Task Login(HttpClient client, string email)
            {
                var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
                var result = await response.Content.ReadFromJsonAsync<LoginResult>(); Assert.NotNull(result);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
            }
            await Login(admin, "pdf-admin@example.test"); await Login(manager, "pdf-manager@example.test"); await Login(outsider, "pdf-other@example.test");
            var path = $"/api/v1/exports/projects/{projectId}/reports/{reportId}.pdf";
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(path)).StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId); tenant.Plan = Plan.Pro; await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path)).StatusCode);
            var responsePdf = await admin.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, responsePdf.StatusCode);
            Assert.Equal("application/pdf", responsePdf.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", responsePdf.Headers.CacheControl?.ToString());
            using var pdf = PdfReader.Open(new MemoryStream(await responsePdf.Content.ReadAsByteArrayAsync()), PdfDocumentOpenMode.ReadOnly);
            Assert.Equal(1, pdf.PageCount);
        }
        finally { foreach (var pair in originals) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
}
