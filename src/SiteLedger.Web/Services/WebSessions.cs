using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace SiteLedger.Web.Services;

public sealed record WebSession(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset AbsoluteExpiry);

public sealed class SessionCoordinator(IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Lazy<ConnectionMultiplexer?> redis = new(() => string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis")) ? null : ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));
    public async Task<IAsyncDisposable> Enter(string key, CancellationToken ct)
    {
        var stripe = stripes[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)stripes.Length];
        await stripe.WaitAsync(ct);
        try
        {
            var db = redis.Value?.GetDatabase();
            var owner = Guid.NewGuid().ToString("N");
            var lockKey = "SiteLedger.Web:lock:" + key;
            if (db is not null)
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!await db.StringSetAsync(lockKey, owner, TimeSpan.FromSeconds(90), When.NotExists))
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Session is busy.");
                    await Task.Delay(100, ct);
                }
            }
            return new Held(stripe, db, lockKey, owner);
        }
        catch { stripe.Release(); throw; }
    }
    private sealed class Held(SemaphoreSlim stripe, IDatabase? db, string key, string owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (db is not null) await db.ScriptEvaluateAsync("if redis.call('get',KEYS[1]) == ARGV[1] then return redis.call('del',KEYS[1]) else return 0 end", [key], [owner]); }
            finally { stripe.Release(); }
        }
    }
    public void Dispose() { if (redis.IsValueCreated) redis.Value?.Dispose(); foreach (var stripe in stripes) stripe.Dispose(); }
}

public sealed class WebSessions(IDistributedCache cache, IDataProtectionProvider protection, SessionCoordinator coordinator)
{
    private readonly IDataProtector protector = protection.CreateProtector("SiteLedger.Web.Session.v1");
    public Task Store(string key, WebSession session) => cache.SetStringAsync(key, protector.Protect(JsonSerializer.Serialize(session)), new DistributedCacheEntryOptions { AbsoluteExpiration = session.AbsoluteExpiry });
    private async Task<WebSession?> Read(string key)
    {
        var raw = await cache.GetStringAsync(key);
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<WebSession>(protector.Unprotect(raw)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { await cache.RemoveAsync(key); return null; }
    }
    public async Task<string?> Access(string key, HttpClient http, CancellationToken ct)
    {
        await using var held = await coordinator.Enter(key, ct);
        var session = await Read(key);
        if (session is null) return null;
        if (session.AbsoluteExpiry <= DateTimeOffset.UtcNow) { await cache.RemoveAsync(key); return null; }
        if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return session.AccessToken;
        using var response = await http.PostAsJsonAsync("api/v1/auth/refresh", new { refreshToken = session.RefreshToken }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { await cache.RemoveAsync(key); return null; }
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var updated = session with { AccessToken = value.GetProperty("accessToken").GetString()!, ExpiresAt = value.GetProperty("expiresAt").GetDateTimeOffset(), RefreshToken = value.GetProperty("refreshToken").GetString()! };
        await Store(key, updated);
        return updated.AccessToken;
    }
    public async Task<bool> Revoke(string key, HttpClient http, CancellationToken ct)
    {
        await using var held = await coordinator.Enter(key, ct);
        var session = await Read(key);
        if (session is null) return true;
        bool revoked;
        try { using var result = await http.PostAsJsonAsync("api/v1/auth/logout", new { refreshToken = session.RefreshToken }, ct); revoked = result.IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { revoked = false; }
        await cache.RemoveAsync(key);
        return revoked;
    }
}
