using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SiteLedger.Api.Auth;
using SiteLedger.Core;
using SiteLedger.Infrastructure;
using Testcontainers.MySql;

namespace SiteLedger.Tests;

public sealed class AuditIntegrationTests
{
    [Fact]
    public async Task AuditIsTenantScopedAndRestrictedToTenantAdministrators()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var values = new Dictionary<string, string?> {
            ["ConnectionStrings__SiteLedger"] = mysql.GetConnectionString(),
            ["Authentication__Jwt__Issuer"] = "audit-test",
            ["Authentication__Jwt__Audience"] = "audit-client",
            ["Authentication__Jwt__SigningKey"] = "audit-signing-key-for-test-1234567890123456" };
        var previous = values.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
        foreach (var value in values) Environment.SetEnvironmentVariable(value.Key, value.Value);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["ConnectionStrings:SiteLedger"] = mysql.GetConnectionString(),
                    ["Authentication:Jwt:Issuer"] = "audit-test", ["Authentication:Jwt:Audience"] = "audit-client",
                    ["Authentication:Jwt:SigningKey"] = "audit-signing-key-for-test-1234567890123456" })));
            using var a = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var b = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var manager = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            const string password = "Valid-test-password-123!";
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                await db.Database.MigrateAsync();
                var tenantA = new Tenant { CompanyName = "A", Slug = "a", Plan = Plan.Pro };
                var tenantB = new Tenant { CompanyName = "B", Slug = "b" };
                db.Tenants.AddRange(tenantA, tenantB); await db.SaveChangesAsync();
                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                Assert.True((await roleManager.CreateAsync(new IdentityRole(Roles.TenantAdmin))).Succeeded);
                Assert.True((await roleManager.CreateAsync(new IdentityRole(Roles.Manager))).Succeeded);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                async Task AddUser(string email, Guid tenantId, string role)
                {
                    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = email, TenantId = tenantId };
                    Assert.True((await users.CreateAsync(user, password)).Succeeded);
                    Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                }
                await AddUser("admin-a@example.test", tenantA.Id, Roles.TenantAdmin);
                await AddUser("admin-b@example.test", tenantB.Id, Roles.TenantAdmin);
                await AddUser("manager@example.test", tenantA.Id, Roles.Manager);
            }
            async Task Login(HttpClient client, string email)
            {
                var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var token = await response.Content.ReadFromJsonAsync<LoginResult>();
                Assert.NotNull(token);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            }
            await Login(a, "admin-a@example.test"); await Login(b, "admin-b@example.test"); await Login(manager, "manager@example.test");
            var project = await a.PostAsJsonAsync("/api/v1/projects", new { name = "Site", clientName = "Client", siteAddress = "Site", budget = 25m, startDate = "2026-01-01", status = 1 });
            Assert.Equal(HttpStatusCode.Created, project.StatusCode);
            var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var report = await a.PostAsJsonAsync($"/api/v1/projects/{projectId}/reports", new { reportDate = "2026-01-02", weather = "Clear", workersOnSite = 2, workSummary = "Sensitive work summary", issues = "None" });
            Assert.Equal(HttpStatusCode.Created, report.StatusCode);
            var reportId = (await report.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/v1/projects/{projectId}/reports/{reportId}/submit", null)).StatusCode);
            var expense = await a.PostAsJsonAsync($"/api/v1/projects/{projectId}/expenses", new { expenseDate = "2026-01-02", category = 0, amount = 12m, supplier = "Supplier" });
            Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
            var expenseId = (await expense.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/v1/projects/{projectId}/expenses/{expenseId}/submit", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/projects?privateQuery=DoNotRecordThis")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/exports/projects.csv")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/v1/exports/projects/{projectId}/reports/{reportId}.pdf")).StatusCode);
            var response = await a.GetAsync("/api/v1/audit?page=1&pageSize=100");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Sensitive work summary", raw);
            Assert.DoesNotContain("DoNotRecordThis", raw);
            var events = JsonSerializer.Deserialize<JsonElement[]>(raw)!;
            var actions = events.Select(e => e.GetProperty("action").GetString()).ToArray();
            Assert.Contains("project.create", actions); Assert.Contains("report.create", actions);
            Assert.Contains("report.submit", actions); Assert.Contains("expense.create", actions); Assert.Contains("expense.submit", actions);
            Assert.Contains("read.started", actions); Assert.Contains("read.completed", actions);
            Assert.Equal(2, actions.Count(x => x == "export.completed"));
            Assert.Contains(events, e => e.GetProperty("action").GetString() == "read.completed" &&
                JsonDocument.Parse(e.GetProperty("details").GetString()!).RootElement.GetProperty("StatusCode").GetInt32() == 403);
            var otherEvents = (await b.GetFromJsonAsync<JsonElement[]>("/api/v1/audit"))!;
            // Reading the audit log is itself audited, but no tenant A workflow leaks into B.
            Assert.NotEmpty(otherEvents);
            Assert.All(otherEvents, e => Assert.StartsWith("read.", e.GetProperty("action").GetString()));
            Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync("/api/v1/audit?pageSize=101")).StatusCode);
        }
        finally { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }
}
