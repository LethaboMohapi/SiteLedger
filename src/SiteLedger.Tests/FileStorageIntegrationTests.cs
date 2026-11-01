using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

[CollectionDefinition("File integration", DisableParallelization = true)]
public sealed class FileIntegrationCollection;

[Collection("File integration")]
public sealed class FileStorageIntegrationTests
{
    private sealed record TestTenant(Guid? TenantId, string? UserId) : ICurrentTenant;

    [Fact]
    public async Task UploadIsPrivateScopedShareableAndReleasesQuota()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "siteledger-file-tests-" + Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string?> {
            ["ConnectionStrings__SiteLedger"] = mysql.GetConnectionString(), ["Authentication__Jwt__Issuer"] = "siteledger-test",
            ["Authentication__Jwt__Audience"] = "siteledger-test-client", ["Authentication__Jwt__SigningKey"] = "test-key-used-only-for-integration-test-123456", ["Storage__Path"] = root };
        var original = values.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
        foreach (var pair in values) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:SiteLedger"] = mysql.GetConnectionString(), ["Authentication:Jwt:Issuer"] = "siteledger-test", ["Authentication:Jwt:Audience"] = "siteledger-test-client", ["Authentication:Jwt:SigningKey"] = "test-key-used-only-for-integration-test-123456", ["Storage:Path"] = root })));
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var outsider = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            const string password = "Valid-test-password-123!";
            Guid tenantA; Guid projectId; string viewerId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                await db.Database.MigrateAsync();
                var a = new Tenant { CompanyName = "A", Slug = "a" }; var b = new Tenant { CompanyName = "B", Slug = "b" };
                db.Tenants.AddRange(a, b); await db.SaveChangesAsync(); tenantA = a.Id;
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                Assert.True((await roles.CreateAsync(new IdentityRole(Roles.TenantAdmin))).Succeeded);
                Assert.True((await roles.CreateAsync(new IdentityRole(Roles.Viewer))).Succeeded);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                async Task<ApplicationUser> Create(string email, Guid tenant, string role)
                {
                    var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, TenantId = tenant, FullName = email };
                    Assert.True((await users.CreateAsync(user, password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded); return user;
                }
                await Create("a@example.test", a.Id, Roles.TenantAdmin);
                await Create("b@example.test", b.Id, Roles.TenantAdmin);
                viewerId = (await Create("viewer@example.test", a.Id, Roles.Viewer)).Id;
            }
            var options = new DbContextOptionsBuilder<SiteLedgerDbContext>().UseMySql(mysql.GetConnectionString(), new MySqlServerVersion(new Version(8, 0, 36))).Options;
            await using (var seeded = new SiteLedgerDbContext(options, new TestTenant(tenantA, "seed")))
            {
                var project = new Project { Name = "Site", ClientName = "Client", SiteAddress = "Site", StartDate = new DateOnly(2026, 1, 1) };
                seeded.Projects.Add(project); seeded.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = viewerId, RoleOnProject = "Viewer" });
                await seeded.SaveChangesAsync(); projectId = project.Id;
            }
            async Task Login(HttpClient client, string address)
            {
                var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = address, password });
                var token = await response.Content.ReadFromJsonAsync<LoginResult>(); Assert.NotNull(token);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            }
            await Login(admin, "a@example.test"); await Login(outsider, "b@example.test"); await Login(viewer, "viewer@example.test");
            var url = $"/api/v1/projects/{projectId}/files";
            using var invalid = new MultipartFormDataContent();
            invalid.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not a PDF")), "file", "fake.pdf"); invalid.Add(new StringContent("4"), "category");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(url, invalid)).StatusCode);
            using var tooLarge = new MultipartFormDataContent();
            tooLarge.Add(new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]), "file", "large.pdf"); tooLarge.Add(new StringContent("4"), "category");
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(url, tooLarge)).StatusCode);
            var bytes = Encoding.UTF8.GetBytes("%PDF-1.4\nSample file\n%%EOF");
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "file", "../../sensitive.pdf"); form.Add(new StringContent("4"), "category");
            var uploaded = await admin.PostAsync(url, form); Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var data = await uploaded.Content.ReadFromJsonAsync<JsonElement>(); var id = data.GetProperty("id").GetGuid();
            Assert.DoesNotContain("/", data.GetProperty("fileName").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{url}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"{url}/{id}")).StatusCode);
            Assert.Equal(bytes, await admin.GetByteArrayAsync($"{url}/{id}"));
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{url}/{id}/sharing", true)).StatusCode);
            Assert.Equal(bytes, await viewer.GetByteArrayAsync($"{url}/{id}"));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{url}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{url}/{id}")).StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                Assert.Equal(0, (await db.Tenants.SingleAsync(t => t.Id == tenantA)).StorageUsedBytes);
                var downloads = await db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == tenantA && x.Action == "file.download.completed").ToListAsync();
                Assert.Equal(4, downloads.Count);
                Assert.All(downloads, item => Assert.DoesNotContain("sensitive.pdf", item.Details));
                Assert.Equal(2, downloads.Count(item => JsonDocument.Parse(item.Details).RootElement.GetProperty("StatusCode").GetInt32() == 200));
            }
        }
        finally
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var checkedRoot = Path.GetFullPath(root);
            if (checkedRoot.StartsWith(tempRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Directory.Exists(checkedRoot)) Directory.Delete(checkedRoot, true);
        }
    }
}
