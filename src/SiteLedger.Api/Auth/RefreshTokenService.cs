using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

// Tokens contain an opaque lookup id and a random secret. The secret is never stored.
public sealed class RefreshTokenService(SiteLedgerDbContext db, DbContextOptions<SiteLedgerDbContext> options,
    UserManager<ApplicationUser> users, IOptions<TokenOptions> settings)
{
    private sealed record TokenTenant(Guid? TenantId, string? UserId) : ICurrentTenant;
    private sealed record Parts(Guid TenantId, Guid Id, string Secret);
    private static Parts? Parse(string value)
    {
        var p = value.Split('.');
        if (p.Length != 3 || !Guid.TryParseExact(p[0], "N", out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParseExact(p[1], "N", out var id) || id == Guid.Empty || p[2].Length != 43 ||
            p[2].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        return new Parts(tenant, id, p[2]);
    }
    private static string Hash(string secret, string stamp) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret + ":" + stamp)));
    private static bool Matches(string stored, string secret, string stamp)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(stored), Convert.FromHexString(Hash(secret, stamp))); }
        catch (FormatException) { return false; }
    }
    private static (RefreshToken Record, string Token) NewToken(ApplicationUser user, Guid family, DateTimeOffset expires)
    {
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new RefreshToken { TenantId = user.TenantId!.Value, UserId = user.Id, FamilyId = family, ExpiresAt = expires, TokenHash = Hash(secret, user.SecurityStamp!) };
        return (token, $"{token.TenantId:N}.{token.Id:N}.{secret}");
    }
    public async Task<string> IssueAsync(ApplicationUser user, CancellationToken ct)
    {
        if (user.TenantId is null || user.SecurityStamp is null) throw new InvalidOperationException("Account cannot receive a refresh token.");
        await using var tenantDb = new SiteLedgerDbContext(options, new TokenTenant(user.TenantId, user.Id));
        var (item, token) = NewToken(user, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(14));
        tenantDb.RefreshTokens.Add(item);
        await tenantDb.SaveChangesAsync(ct);
        return token;
    }
    private async Task<(Parts Parts, ApplicationUser User)?> Identify(string raw, CancellationToken ct)
    {
        var parts = Parse(raw);
        if (parts is null) return null;
        // The untrusted tenant and id are lookup hints only; possession of the secret is verified below.
        var row = await db.RefreshTokens.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == parts.TenantId && x.Id == parts.Id && !x.IsDeleted, ct);
        if (row is null) return null;
        var user = await users.FindByIdAsync(row.UserId);
        if (user is null || user.TenantId != parts.TenantId || user.SecurityStamp is null || !Matches(row.TokenHash, parts.Secret, user.SecurityStamp)) return null;
        return (parts, user);
    }
    public async Task<LoginResult?> RotateAsync(string raw, CancellationToken ct)
    {
        var identified = await Identify(raw, ct);
        if (identified is null) return null;
        var (parts, user) = identified.Value;
        if (!user.IsActive || !user.EmailConfirmed || await users.IsLockedOutAsync(user)) return null;
        await using var tenantDb = new SiteLedgerDbContext(options, new TokenTenant(parts.TenantId, user.Id));
        await using var transaction = await tenantDb.Database.BeginTransactionAsync(ct);
        await tenantDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {parts.TenantId}", ct);
        if (!await tenantDb.Tenants.AnyAsync(x => x.Id == parts.TenantId && x.SubscriptionStatus == SubscriptionStatus.Active, ct)) return null;
        var item = await tenantDb.RefreshTokens.SingleOrDefaultAsync(x => x.Id == parts.Id && x.UserId == user.Id, ct);
        if (item is null || !Matches(item.TokenHash, parts.Secret, user.SecurityStamp!)) return null;
        var now = DateTimeOffset.UtcNow;
        if (item.RevokedAt.HasValue)
        {
            // Replay invalidates every token in this family, including a concurrent replacement.
            var family = await tenantDb.RefreshTokens.Where(x => x.FamilyId == item.FamilyId && x.UserId == user.Id && x.RevokedAt == null).ToListAsync(ct);
            foreach (var token in family) token.RevokedAt = now;
            await tenantDb.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        if (item.ExpiresAt <= now) return null;
        var (replacement, tokenValue) = NewToken(user, item.FamilyId, item.ExpiresAt);
        item.RevokedAt = now; item.ReplacedByHash = replacement.TokenHash;
        tenantDb.RefreshTokens.Add(replacement);
        await tenantDb.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var access = await LoginService.IssueAccessAsync(user, users, settings.Value);
        return access with { RefreshToken = tokenValue };
    }
    public async Task RevokeAsync(string raw, CancellationToken ct)
    {
        var identified = await Identify(raw, ct);
        if (identified is null) return;
        var (parts, user) = identified.Value;
        await using var tenantDb = new SiteLedgerDbContext(options, new TokenTenant(parts.TenantId, user.Id));
        await using var transaction = await tenantDb.Database.BeginTransactionAsync(ct);
        await tenantDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE `Tenants` SET `StorageUsedBytes` = `StorageUsedBytes` WHERE `Id` = {parts.TenantId}", ct);
        var item = await tenantDb.RefreshTokens.SingleOrDefaultAsync(x => x.Id == parts.Id && x.UserId == user.Id, ct);
        if (item is null || !Matches(item.TokenHash, parts.Secret, user.SecurityStamp!)) return;
        var family = await tenantDb.RefreshTokens.Where(x => x.FamilyId == item.FamilyId && x.UserId == user.Id && x.RevokedAt == null).ToListAsync(ct);
        foreach (var token in family) token.RevokedAt = DateTimeOffset.UtcNow;
        await tenantDb.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
