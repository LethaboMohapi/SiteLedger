using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

// Each operation verifies the password; setup never returns a key for an enabled account.
public sealed class AuthenticatorService(UserManager<ApplicationUser> users, SiteLedgerDbContext db, IUserStore<ApplicationUser> userStore)
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
        if (!enabled)
        {
            if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded) return false;
            if (userStore is IUserTwoFactorRecoveryCodeStore<ApplicationUser> recoveryStore)
            {
                await recoveryStore.ReplaceCodesAsync(user, Array.Empty<string>(), ct);
                if (!(await users.UpdateAsync(user)).Succeeded) return false;
            }
        }
        await users.ResetAccessFailedCountAsync(user);
        return true;
    }
    public static string HashRecoveryCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    public async Task<IReadOnlyList<string>?> GenerateRecoveryCodes(string email, string password, string code, CancellationToken ct)
    {
        var user = await VerifyPassword(email, password, ct);
        if (user is null || !user.TwoFactorEnabled || string.IsNullOrWhiteSpace(code)) return null;
        if (!await users.VerifyTwoFactorTokenAsync(user, Microsoft.AspNetCore.Identity.TokenOptions.DefaultAuthenticatorProvider, code.Replace(" ", "").Replace("-", "")))
        { await users.AccessFailedAsync(user); return null; }
        var store = userStore as IUserTwoFactorRecoveryCodeStore<ApplicationUser>;
        if (store is null) return null;
        var codes = Enumerable.Range(0, 10).Select(_ => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))).ToArray();
        await store.ReplaceCodesAsync(user, codes.Select(HashRecoveryCode), ct);
        if (!(await users.UpdateAsync(user)).Succeeded) return null;
        await users.ResetAccessFailedCountAsync(user);
        return codes;
    }

}
