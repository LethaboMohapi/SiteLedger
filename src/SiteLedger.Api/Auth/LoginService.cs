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

public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAt);

public interface ILoginService
{
    Task<LoginResult?> LoginAsync(string email, string password, string? authenticatorCode, CancellationToken cancellationToken);
}

public sealed class LoginService(UserManager<ApplicationUser> users,
    SiteLedgerDbContext db, IOptions<TokenOptions> options) : ILoginService
{
    public async Task<LoginResult?> LoginAsync(string email, string password, string? authenticatorCode, CancellationToken cancellationToken)
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
        if (user.TwoFactorEnabled && (string.IsNullOrWhiteSpace(authenticatorCode) ||
            !await users.VerifyTwoFactorTokenAsync(user, Microsoft.AspNetCore.Identity.TokenOptions.DefaultAuthenticatorProvider, authenticatorCode.Replace(" ", "").Replace("-", ""))))
        {
            await users.AccessFailedAsync(user);
            return null;
        }
        await users.ResetAccessFailedCountAsync(user);
        var roles = await users.GetRolesAsync(user);
        var settings = options.Value;
        var expires = DateTimeOffset.UtcNow.AddMinutes(settings.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.FullName),
            new("tenant_id", user.TenantId.Value.ToString()),
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
