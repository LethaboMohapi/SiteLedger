using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

// Each operation verifies the password; setup never returns a key for an enabled account.
public sealed class AuthenticatorService(UserManager<ApplicationUser> users, SiteLedgerDbContext db)
{
    private async Task<ApplicationUser?> VerifyPassword(string email, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.IsActive || !user.EmailConfirmed || user.TenantId is null || await users.IsLockedOutAsync(user)) return null;
        if (!await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.SubscriptionStatus == SubscriptionStatus.Active, ct)) return null;
        if (!await users.CheckPasswordAsync(user, password)) { await users.AccessFailedAsync(user); return null; }
        return user;
    }

    public async Task<string?> Setup(string email, string password, CancellationToken ct)
    {
        var user = await VerifyPassword(email, password, ct);
        if (user is null || user.TwoFactorEnabled) return null;
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (key is null)
        {
            if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return null;
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        await users.ResetAccessFailedCountAsync(user);
        return key;
    }

    public async Task<bool> SetEnabled(string email, string password, string code, bool enabled, CancellationToken ct)
    {
        var user = await VerifyPassword(email, password, ct);
        if (user is null || user.TwoFactorEnabled == enabled || string.IsNullOrWhiteSpace(code)) return false;
        var valid = await users.VerifyTwoFactorTokenAsync(user, Microsoft.AspNetCore.Identity.TokenOptions.DefaultAuthenticatorProvider, code.Replace(" ", "").Replace("-", ""));
        if (!valid) { await users.AccessFailedAsync(user); return false; }
        if (!(await users.SetTwoFactorEnabledAsync(user, enabled)).Succeeded) return false;
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return false;
        if (!enabled && !(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return false;
        await users.ResetAccessFailedCountAsync(user);
        return true;
    }
}
