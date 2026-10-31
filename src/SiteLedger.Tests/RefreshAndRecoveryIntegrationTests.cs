using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace SiteLedger.Tests;

public sealed class RefreshAndRecoveryIntegrationTests
{
    private static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var buffer = 0; var bytes = new List<byte>();
        foreach (var c in key.ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1; }
        }
        var counter = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter); var offset = hash[^1] & 15;
        return ((BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff) % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task RecoveryCodesAreSingleUseAndRefreshReplayRevokesFamily()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var vars = new Dictionary<string, string?>
        {
            ["ConnectionStrings__SiteLedger"] = mysql.GetConnectionString(),
            ["Authentication__Jwt__Issuer"] = "recovery-test",
            ["Authentication__Jwt__Audience"] = "recovery-client",
            ["Authentication__Jwt__SigningKey"] = "test-signing-key-for-authentication-12345678"
        };
        var old = vars.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
        foreach (var pair in vars) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SiteLedger"] = mysql.GetConnectionString(), ["Authentication:Jwt:Issuer"] = "recovery-test", ["Authentication:Jwt:Audience"] = "recovery-client", ["Authentication:Jwt:SigningKey"] = "test-signing-key-for-authentication-12345678" })));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            const string email = "auth@example.test", password = "Valid-password-123!";
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>();
                await db.Database.MigrateAsync();
                var tenant = new Tenant { CompanyName = "Test", Slug = "recovery" };
                db.Tenants.Add(tenant); await db.SaveChangesAsync();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                Assert.True((await roles.CreateAsync(new IdentityRole(Roles.TenantAdmin))).Succeeded);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Test", TenantId = tenant.Id };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, Roles.TenantAdmin)).Succeeded);
            }
            var setup = await client.PostAsJsonAsync("/api/v1/auth/authenticator/setup", new { email, password });
            var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/authenticator/enable", new { email, password, code = Totp(key) })).StatusCode);
            var recovery = await client.PostAsJsonAsync("/api/v1/auth/authenticator/recovery-codes", new { email, password, code = Totp(key) });
            Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
            var code = (await recovery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codes")[0].GetString()!;
            var first = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, recoveryCode = code });
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, recoveryCode = code })).StatusCode);
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, authenticatorCode = Totp(key) });
            var loginResult = await login.Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(loginResult);
            Assert.NotNull(loginResult.RefreshToken);
            var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = loginResult.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            var next = await rotated.Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(next);
            Assert.NotNull(next.RefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = loginResult.RefreshToken })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = next.RefreshToken })).StatusCode);
            await using var secondFactory = factory.WithWebHostBuilder(_ => { });
            using var second = secondFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var last = await (await second.PostAsJsonAsync("/api/v1/auth/login", new { email, password, authenticatorCode = Totp(key) })).Content.ReadFromJsonAsync<LoginResult>();
            Assert.NotNull(last);
            Assert.NotNull(last.RefreshToken);
            Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = last.RefreshToken })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = last.RefreshToken })).StatusCode);
        }
        finally { foreach (var pair in old) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
}
