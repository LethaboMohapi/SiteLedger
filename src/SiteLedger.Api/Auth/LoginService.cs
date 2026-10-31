using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken = null);

public interface ILoginService
{
    Task<LoginResult?> LoginAsync(string email, string password, string? authenticatorCode, string? recoveryCode, CancellationToken cancellationToken);
}

public sealed class LoginService(UserManager<ApplicationUser> users,
    SiteLedgerDbContext db, IOptions<TokenOptions> options, RefreshTokenService refresh) : ILoginService
{
    public async Task<LoginResult?> LoginAsync(string email, string password, string? authenticatorCode, string? recoveryCode, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.IsActive || !user.EmailConfirmed || user.TenantId is null || await users.IsLockedOutAsync(user)) return null;
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.TenantId, cancellationToken);
        if (tenant is null || tenant.SubscriptionStatus != SubscriptionStatus.Active) return null;
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }
        if (user.TwoFactorEnabled)
        {
            var valid = !string.IsNullOrWhiteSpace(authenticatorCode) &&
                await users.VerifyTwoFactorTokenAsync(user, Microsoft.AspNetCore.Identity.TokenOptions.DefaultAuthenticatorProvider, authenticatorCode.Replace(" ", "").Replace("-", ""));
            if (!valid && !string.IsNullOrWhiteSpace(recoveryCode))
                valid = (await users.RedeemTwoFactorRecoveryCodeAsync(user, AuthenticatorService.HashRecoveryCode(recoveryCode))).Succeeded;
            if (!valid) { await users.AccessFailedAsync(user); return null; }
        }
        await users.ResetAccessFailedCountAsync(user);
        var tokenResult = await IssueAccessAsync(user, users, options.Value);
        var refreshToken = await refresh.IssueAsync(user, cancellationToken);
        return tokenResult with { RefreshToken = refreshToken };
    }

    internal static async Task<LoginResult> IssueAccessAsync(ApplicationUser user, UserManager<ApplicationUser> users, TokenOptions settings)
    {
        var tenantId = user.TenantId ?? throw new InvalidOperationException("Account has no tenant.");
        var roles = await users.GetRolesAsync(user);
        var expires = DateTimeOffset.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.FullName),
            new("tenant_id", tenantId.ToString()),
            new("security_stamp", user.SecurityStamp ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new LoginResult(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
