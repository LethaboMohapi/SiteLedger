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

public sealed class LoginIntegrationTests
{
    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();
        public void Set(string name, string value)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        public void Dispose()
        {
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    [Fact]
    public async Task ConfirmedUserCanLogInAndDeactivationRevokesAccess()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        using var environment = new TemporaryEnvironment();
        environment.Set("ConnectionStrings__SiteLedger", mysql.GetConnectionString());
        environment.Set("Authentication__Jwt__Issuer", "siteledger-test");
        environment.Set("Authentication__Jwt__Audience", "siteledger-test-client");
        environment.Set("Authentication__Jwt__SigningKey", "test-key-used-only-for-integration-test-123456");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SiteLedger"] = mysql.GetConnectionString(),
                ["Authentication:Jwt:Issuer"] = "siteledger-test",
                ["Authentication:Jwt:Audience"] = "siteledger-test-client",
                ["Authentication:Jwt:SigningKey"] = "test-key-used-only-for-integration-test-123456"
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        const string email = "member@example.test";
        const string password = "Valid-test-password-123!";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
            await db.Database.MigrateAsync();
            var tenant = new Tenant { CompanyName = "Test", Slug = "test" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            Assert.True((await roles.CreateAsync(new IdentityRole(Roles.Member))).Succeeded);
            Assert.True((await roles.CreateAsync(new IdentityRole(Roles.Manager))).Succeeded);
            Assert.True((await roles.CreateAsync(new IdentityRole(Roles.TenantAdmin))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Test member", TenantId = tenant.Id };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Member)).Succeeded);
            var reviewer = new ApplicationUser { UserName = "reviewer@example.test", Email = "reviewer@example.test", EmailConfirmed = true, FullName = "Reviewer", TenantId = tenant.Id };
            Assert.True((await users.CreateAsync(reviewer, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(reviewer, Roles.TenantAdmin)).Succeeded);
        }
        var incorrect = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "incorrect" });
        Assert.Equal(HttpStatusCode.Unauthorized, incorrect.StatusCode);
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var project = new { name = "First", clientName = "Client", siteAddress = "Site", budget = 100m, startDate = "2026-01-01", status = 1 };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/projects", project)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.True((await users.RemoveFromRoleAsync(user, Roles.Member)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Manager)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        token = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var firstProject = await client.PostAsJsonAsync("/api/v1/projects", project);
        Assert.Equal(HttpStatusCode.Created, firstProject.StatusCode);
        var firstId = (await firstProject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var taskInput = new { title = "Inspect site", description = "Initial inspection", priority = 1, status = 0, percentComplete = 0 };
        var taskResponse = await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/tasks", taskInput);
        Assert.Equal(HttpStatusCode.Created, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = task.GetProperty("id").GetGuid();
        var version = task.GetProperty("rowVersion").GetGuid();
        var updatedTask = new { title = "Inspect site thoroughly", description = "Initial inspection", priority = 1, status = 0, percentComplete = 0 };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/tasks/{taskId}?version={version}", updatedTask)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/tasks/{taskId}?version={version}", taskInput)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}/tasks")).StatusCode);
        var reportResponse = await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports", new { reportDate = "2026-01-02", weather = "Clear", workersOnSite = 3, workSummary = "Foundation work", issues = "None" });
        Assert.Equal(HttpStatusCode.Created, reportResponse.StatusCode);
        var reportId = (await reportResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/projects/{firstId}/reports/{reportId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/projects/{firstId}/reports/{reportId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Approved" })).StatusCode);
        using var reviewerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var reviewerLogin = await reviewerClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reviewer@example.test", password });
        var reviewerToken = await reviewerLogin.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(reviewerToken);
        reviewerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reviewerToken.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await reviewerClient.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await reviewerClient.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}/reports")).StatusCode);
        var expenseResponse = await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/expenses", new { expenseDate = "2026-01-02", category = 0, amount = 125.50m, supplier = "Supplier" });
        Assert.Equal(HttpStatusCode.Created, expenseResponse.StatusCode);
        var expenseId = (await expenseResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/projects/{firstId}/expenses/{expenseId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/expenses", new { expenseDate = "2026-01-02", category = 0, amount = -1, supplier = "Supplier" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Second", clientName = "Client", siteAddress = "Site", budget = 50m, startDate = "2026-01-01", status = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Third", clientName = "Client", siteAddress = "Site", budget = 50m, startDate = "2026-01-01", status = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/projects")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            user.IsActive = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
