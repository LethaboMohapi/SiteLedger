using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SiteLedger.Web.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Testcontainers.Redis;

namespace SiteLedger.Tests;

public sealed class WebSessionTests
{
    private sealed class Transport : HttpMessageHandler
    {
        public int Refreshes;
        public string? Revoked;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("refresh"))
            {
                Interlocked.Increment(ref Refreshes);
                await Task.Delay(30, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = "new-access", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), refreshToken = "new-refresh" }) };
            }
            var body = await request.Content!.ReadAsStringAsync(ct);
            Revoked = body;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
    [Fact]
    public async Task ConcurrentRequestsRotateOnceAndLogoutRevokesLatestSecret()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        using var coordinator = new SessionCoordinator(new ConfigurationBuilder().Build());
        var sessions = new WebSessions(cache, new EphemeralDataProtectionProvider(), coordinator);
        using var transport = new Transport(); using var http = new HttpClient(transport) { BaseAddress = new Uri("https://localhost") };
        await sessions.Store("test", new WebSession("old-access", DateTimeOffset.UtcNow.AddMinutes(-1), "old-refresh", DateTimeOffset.UtcNow.AddDays(1)));
        Assert.DoesNotContain("old-refresh", await cache.GetStringAsync("test"));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sessions.Access("test", http, default)));
        Assert.All(results, token => Assert.Equal("new-access", token));
        Assert.Equal(1, transport.Refreshes);
        Assert.True(await sessions.Revoke("test", http, default));
        Assert.Contains("new-refresh", transport.Revoked);
        Assert.Null(await sessions.Access("test", http, default));
    }
    [Fact]
    public async Task TwoInstancesShareEncryptedSessionsAndSerializeRefreshInRedis()
    {
        await using var redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await redis.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis.GetConnectionString() }).Build();
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redis.GetConnectionString() }));
        using var firstLock = new SessionCoordinator(configuration); using var secondLock = new SessionCoordinator(configuration);
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "siteledger-keys-" + Guid.NewGuid().ToString("N")));
        directory.Create();
        try
        {
            var firstProtection = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("SessionTest"));
            var first = new WebSessions(cache, firstProtection, firstLock);
            await first.Store("shared", new WebSession("old-access", DateTimeOffset.UtcNow.AddMinutes(-1), "old-refresh", DateTimeOffset.UtcNow.AddHours(1)));
            var secondProtection = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("SessionTest"));
            var second = new WebSessions(cache, secondProtection, secondLock);
            using var transport = new Transport(); using var http = new HttpClient(transport) { BaseAddress = new Uri("https://localhost") };
            var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? first : second).Access("shared", http, default)));
            Assert.All(tokens, value => Assert.Equal("new-access", value));
            Assert.Equal(1, transport.Refreshes);
            Assert.True(await second.Revoke("shared", http, default));
            Assert.Null(await first.Access("shared", http, default));
        }
        finally { if (directory.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && directory.Name.StartsWith("siteledger-keys-", StringComparison.Ordinal)) directory.Delete(true); }
    }

}

