using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using SiteLedger.Web.Services;
using System.Security.Cryptography;
using System.Buffers.Binary;
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
    private static string FormToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "The form must contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string AuthenticatorCode(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var buffer = 0; var bytes = new List<byte>();
        foreach (var ch in key.ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1; }
        }
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % 1_000_000).ToString("D6");
    }

    private sealed class CapturedEmail : IAccountEmailSender
    {
        public bool IsConfigured => true;
        public string Body { get; private set; } = "";
        public Task SendAsync(string address, string subject, string body, CancellationToken ct) { Body = body; return Task.CompletedTask; }
    }

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
        var capturedEmail = new CapturedEmail();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<IAccountEmailSender>(capturedEmail));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SiteLedger"] = mysql.GetConnectionString(),
                ["Authentication:Jwt:Issuer"] = "siteledger-test",
                ["Authentication:Jwt:Audience"] = "siteledger-test-client",
                ["Authentication:Jwt:SigningKey"] = "test-key-used-only-for-integration-test-123456",
                ["Account:PublicBaseUrl"] = "https://localhost"
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        const string email = "member@example.test";
        const string password = "Valid-test-password-123!";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
            await db.Database.MigrateAsync();
            var tenant = new Tenant { CompanyName = "Test", Slug = "test" };
            db.Tenants.Add(tenant);
            var otherTenant = new Tenant { CompanyName = "Other", Slug = "other" };
            db.Tenants.Add(otherTenant);
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
            var outsider = new ApplicationUser { UserName = "outsider@example.test", Email = "outsider@example.test", EmailConfirmed = true, FullName = "Outsider", TenantId = otherTenant.Id };
            Assert.True((await users.CreateAsync(outsider, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(outsider, Roles.Member)).Succeeded);
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
        var firstProjectData = await firstProject.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstProjectData.GetProperty("id").GetGuid();
        var projectVersion = firstProjectData.GetProperty("rowVersion").GetGuid();
        var projectUpdate = new { name = "=First updated", clientName = "Client", siteAddress = "Site", budget = 150m, startDate = "2026-01-01", status = 1 };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}?version={projectVersion}", projectUpdate)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}?version={projectVersion}", projectUpdate)).StatusCode);
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
        var reportObject = await reportResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reportId = reportObject.GetProperty("id").GetGuid();
        var reportVersion = reportObject.GetProperty("rowVersion").GetGuid();
        var reportEdit = new { reportDate = "2026-01-02", weather = "Cloudy", workersOnSite = 4, workSummary = "Foundation work", issues = "None" };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}?version={reportVersion}", reportEdit)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}?version={reportVersion}", reportEdit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/projects/{firstId}/reports/{reportId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/projects/{firstId}/reports/{reportId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Approved" })).StatusCode);
        using var reviewerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var reviewerLogin = await reviewerClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reviewer@example.test", password });
        var reviewerToken = await reviewerLogin.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(reviewerToken);
        reviewerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reviewerToken.AccessToken);
        string reviewerId, outsiderId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            reviewerId = (await users.FindByEmailAsync("reviewer@example.test"))!.Id;
            outsiderId = (await users.FindByEmailAsync("outsider@example.test"))!.Id;
        }
        var memberUrl = $"/api/v1/projects/{firstId}/members";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(memberUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reviewerClient.PutAsJsonAsync(memberUrl, new { userId = outsiderId, roleOnProject = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reviewerClient.PutAsJsonAsync(memberUrl, new { userId = reviewerId, roleOnProject = "Reviewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reviewerClient.DeleteAsync($"{memberUrl}/{reviewerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reviewerClient.PutAsJsonAsync(memberUrl, new { userId = reviewerId, roleOnProject = "Reviewer" })).StatusCode);
        using var outsiderClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var outsiderLogin = await outsiderClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "outsider@example.test", password });
        var outsiderToken = await outsiderLogin.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(outsiderToken);
        outsiderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderToken.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/api/v1/projects/{firstId}/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.GetAsync($"/api/v1/projects/{firstId}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reviewerClient.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await reviewerClient.PostAsJsonAsync($"/api/v1/projects/{firstId}/reports/{reportId}/review", new { approve = true, comment = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}/reports")).StatusCode);
        var expenseResponse = await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/expenses", new { expenseDate = "2026-01-02", category = 0, amount = 125.50m, supplier = "Supplier" });
        Assert.Equal(HttpStatusCode.Created, expenseResponse.StatusCode);
        var expenseData = await expenseResponse.Content.ReadFromJsonAsync<JsonElement>();
        var expenseId = expenseData.GetProperty("id").GetGuid();
        var expenseVersion = expenseData.GetProperty("rowVersion").GetGuid();
        var expenseUpdate = new { expenseDate = "2026-01-02", category = 0, amount = 150m, supplier = "Supplier" };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/expenses/{expenseId}?version={expenseVersion}", expenseUpdate)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/projects/{firstId}/expenses/{expenseId}?version={expenseVersion}", expenseUpdate)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/projects/{firstId}/expenses/{expenseId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/projects/{firstId}/expenses", new { expenseDate = "2026-01-02", category = 0, amount = -1, supplier = "Supplier" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Second", clientName = "Client", siteAddress = "Site", budget = 50m, startDate = "2026-01-01", status = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/projects", new { name = "Third", clientName = "Client", siteAddress = "Site", budget = 50m, startDate = "2026-01-01", status = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewerClient.GetAsync("/api/v1/exports/projects.csv")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Slug == "test");
            tenant.Plan = Plan.Pro;
            await db.SaveChangesAsync();
        }
        var csvResponse = await reviewerClient.GetAsync("/api/v1/exports/projects.csv");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"'=First updated\"", csv);
        Assert.Contains("\"Second\"", csv);
        Assert.Equal(HttpStatusCode.Created, (await reviewerClient.PostAsJsonAsync("/api/v1/projects", new { name = "Admin only", clientName = "Client", siteAddress = "Site", budget = 25m, startDate = "2026-01-01", status = 1 })).StatusCode);
        var managerCsv = await client.GetAsync("/api/v1/exports/projects.csv");
        Assert.Equal(HttpStatusCode.OK, managerCsv.StatusCode);
        var managerBody = await managerCsv.Content.ReadAsStringAsync();
        Assert.Contains("\"Second\"", managerBody);
        Assert.DoesNotContain("Admin only", managerBody);
        Assert.Contains("Admin only", await reviewerClient.GetStringAsync("/api/v1/exports/projects.csv"));
        var dashboard = await reviewerClient.GetFromJsonAsync<JsonElement>("/api/v1/dashboard");
        Assert.Equal(3, dashboard.GetProperty("projects").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("openTasks").GetInt32());
        var outsiderDashboard = await outsiderClient.GetFromJsonAsync<JsonElement>("/api/v1/dashboard");
        Assert.Equal(0, outsiderDashboard.GetProperty("projects").GetInt32());
        var reportsCsv = await client.GetAsync($"/api/v1/exports/projects/{firstId}/reports.csv");
        Assert.Equal(HttpStatusCode.OK, reportsCsv.StatusCode);
        Assert.Contains("Foundation work", await reportsCsv.Content.ReadAsStringAsync());
        var expensesCsv = await client.GetAsync($"/api/v1/exports/projects/{firstId}/expenses.csv");
        Assert.Equal(HttpStatusCode.OK, expensesCsv.StatusCode);
        Assert.Contains("Supplier", await expensesCsv.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/v1/exports/projects/{firstId}/expenses.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync("/api/v1/exports/projects.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/projects")).StatusCode);
        await using var authFactory = factory.WithWebHostBuilder(_ => { });
        using var authClient = authFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reviewerToken.AccessToken);
        var setup = await authClient.PostAsJsonAsync("/api/v1/auth/authenticator/setup", new { email = "reviewer@example.test", password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var code = AuthenticatorCode(key);
        Assert.Equal(HttpStatusCode.NoContent, (await authClient.PostAsJsonAsync("/api/v1/auth/authenticator/enable", new { email = "reviewer@example.test", password, code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reviewer@example.test", password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reviewer@example.test", password, authenticatorCode = "invalid" })).StatusCode);
        var twoFactorLogin = await authClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "reviewer@example.test", password, authenticatorCode = AuthenticatorCode(key) });
        Assert.Equal(HttpStatusCode.OK, twoFactorLogin.StatusCode);
        var twoFactorToken = await twoFactorLogin.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(twoFactorToken);
        authClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", twoFactorToken.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await authClient.PostAsJsonAsync("/api/v1/auth/authenticator/disable", new { email = "reviewer@example.test", password, code = AuthenticatorCode(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authClient.GetAsync("/api/v1/auth/me")).StatusCode);
        await using var registrationFactory = factory.WithWebHostBuilder(_ => { });
        using var registrationClient = registrationFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        const string newEmail = "new@example.test";
        const string newPassword = "Another-valid-password-123!";
        Assert.Equal(HttpStatusCode.Accepted, (await registrationClient.PostAsJsonAsync("/api/v1/account/register", new { email = newEmail, password, fullName = "New User", companyName = "New Company" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await registrationClient.PostAsJsonAsync("/api/v1/auth/login", new { email = newEmail, password })).StatusCode);
        var confirmUrl = new Uri(capturedEmail.Body.Split(' ').Last());
        Assert.Equal(HttpStatusCode.OK, (await registrationClient.GetAsync(confirmUrl.PathAndQuery)).StatusCode);
        var registeredLogin = await registrationClient.PostAsJsonAsync("/api/v1/auth/login", new { email = newEmail, password });
        Assert.Equal(HttpStatusCode.OK, registeredLogin.StatusCode);
        var registeredToken = await registeredLogin.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(registeredToken);
        registrationClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registeredToken.AccessToken);
        Assert.Equal(HttpStatusCode.Accepted, (await registrationClient.PostAsJsonAsync("/api/v1/account/password/forgot", new { email = newEmail })).StatusCode);
        var resetToken = capturedEmail.Body.Split(' ').Last();
        Assert.Equal(HttpStatusCode.OK, (await registrationClient.PostAsJsonAsync("/api/v1/account/password/reset", new { email = newEmail, token = resetToken, password = newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await registrationClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await registrationClient.PostAsJsonAsync("/api/v1/auth/login", new { email = newEmail, password = newPassword })).StatusCode);
        await using var webApiFactory = factory.WithWebHostBuilder(_ => { });
        _ = webApiFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await using var webFactory = new WebApplicationFactory<SiteLedger.Web.WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.AddHttpClient<ApiClient>().ConfigurePrimaryHttpMessageHandler(() => webApiFactory.Server.CreateHandler()));
        });
        using var webClient = webFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var loginHtml = await webClient.GetStringAsync("/Account/Login");
        var webLogin = await webClient.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormToken(loginHtml), ["email"] = newEmail, ["password"] = newPassword
        }));
        Assert.Equal(HttpStatusCode.Redirect, webLogin.StatusCode);
        Assert.Equal("/Projects", webLogin.Headers.Location?.OriginalString);
        var projectsHtml = await webClient.GetStringAsync("/Projects");
        var missingCsrf = await webClient.PostAsync("/Projects/Create", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var createWebProject = await webClient.PostAsync("/Projects/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormToken(projectsHtml), ["name"] = "MVC project", ["clientName"] = "Client", ["siteAddress"] = "Site", ["budget"] = "200", ["startDate"] = "2026-01-01", ["status"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, createWebProject.StatusCode);
        projectsHtml = await webClient.GetStringAsync("/Projects");
        Assert.Contains("MVC project", projectsHtml);
        var detailPath = Regex.Match(projectsHtml, "href=\"(/Projects/Detail/[0-9a-fA-F-]+)\"").Groups[1].Value;
        Assert.NotEmpty(detailPath);
        var detailHtml = await webClient.GetStringAsync(detailPath);
        Assert.Contains("Site reports", detailHtml);
        var webProjectId = detailPath.Split('/').Last();
        var createWebTask = await webClient.PostAsync("/Projects/Task", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = FormToken(detailHtml), ["id"] = webProjectId, ["title"] = "MVC task", ["description"] = "Inspect", ["priority"] = "0", ["status"] = "0", ["percentComplete"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, createWebTask.StatusCode);
        Assert.Contains("MVC task", await webClient.GetStringAsync(detailPath));
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
