using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Infrastructure;

namespace SiteLedger.Api.Auth;

public sealed record PendingEmail(string Address, string Subject, string Body);

// The scoped DbContext participates in any registration or invitation transaction.
public sealed class OutboxAccountEmailSender(SiteLedgerDbContext db, IDataProtectionProvider protection, IAccountEmailTransport smtp) : IAccountEmailSender
{
    public bool IsConfigured => smtp.IsConfigured;
    public async Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new PendingEmail(address, subject, body));
        db.EmailOutbox.Add(new EmailOutboxMessage { CreatedAt = now, ExpiresAt = now.AddHours(23), NextAttemptAt = now,
            ProtectedPayload = protection.CreateProtector("SiteLedger.EmailOutbox.v1").Protect(payload) });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EmailOutboxDispatcher(IServiceScopeFactory scopes, ILogger<EmailOutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var worker = new EmailOutboxWorker(scope.ServiceProvider.GetRequiredService<SiteLedgerDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), scope.ServiceProvider.GetRequiredService<IAccountEmailTransport>(), logger);
                if (!await worker.ProcessOneAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Email queue processing failed: {ErrorType}", ex.GetType().Name); await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        }
    }

    public sealed class EmailOutboxWorker(SiteLedgerDbContext db, IDataProtectionProvider protection, IAccountEmailTransport smtp, ILogger logger)
    {
        public async Task<bool> ProcessOneAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = await db.EmailOutbox.AsNoTracking().Where(m => m.DeliveredAt == null && m.AbandonedAt == null &&
                m.NextAttemptAt <= now && (m.LeaseUntil == null || m.LeaseUntil < now)).OrderBy(m => m.NextAttemptAt).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
            if (candidate is null) return false;
            var leaseId = Guid.NewGuid();
            var claimed = await db.EmailOutbox.Where(m => m.Id == candidate && m.DeliveredAt == null && m.AbandonedAt == null &&
                m.NextAttemptAt <= now && (m.LeaseUntil == null || m.LeaseUntil < now)).ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.LeaseId, leaseId).SetProperty(m => m.LeaseUntil, now.Add(Lease))
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1), ct);
            if (claimed != 1) return true;
            var message = await db.EmailOutbox.AsNoTracking().SingleAsync(m => m.Id == candidate && m.LeaseId == leaseId, ct);
            if (message.ExpiresAt <= DateTimeOffset.UtcNow || message.ProtectedPayload is null)
            {
                await db.EmailOutbox.Where(m => m.Id == candidate && m.LeaseId == leaseId).ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.AbandonedAt, DateTimeOffset.UtcNow).SetProperty(m => m.ProtectedPayload, (string?)null)
                    .SetProperty(m => m.LeaseId, (Guid?)null).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
                return true;
            }
            try
            {
                var json = protection.CreateProtector("SiteLedger.EmailOutbox.v1").Unprotect(message.ProtectedPayload);
                var mail = JsonSerializer.Deserialize<PendingEmail>(json) ?? throw new InvalidDataException("Invalid queued email.");
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                await smtp.SendAsync(mail.Address, mail.Subject, mail.Body, sendTimeout.Token);
                await db.EmailOutbox.Where(m => m.Id == candidate && m.LeaseId == leaseId).ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.DeliveredAt, DateTimeOffset.UtcNow).SetProperty(m => m.ProtectedPayload, (string?)null)
                    .SetProperty(m => m.LeaseId, (Guid?)null).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not CryptographicException and not JsonException and not InvalidDataException)
            {
                logger.LogWarning("Email delivery attempt failed: {ErrorType}", ex.GetType().Name);
                var delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(message.Attempts - 1, 7))));
                var retryAt = DateTimeOffset.UtcNow.Add(delay);
                await db.EmailOutbox.Where(m => m.Id == candidate && m.LeaseId == leaseId).ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.NextAttemptAt, retryAt)
                    .SetProperty(m => m.LeaseId, (Guid?)null).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
            {
                // Preserve encrypted data for repair until expiry (e.g. key store temporarily unavailable).
                logger.LogError("Queued email could not be decoded: {ErrorType}", ex.GetType().Name);
                var retryAt = DateTimeOffset.UtcNow.AddMinutes(10);
                await db.EmailOutbox.Where(m => m.Id == candidate && m.LeaseId == leaseId).ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.NextAttemptAt, retryAt)
                    .SetProperty(m => m.LeaseId, (Guid?)null).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
            }
            return true;
        }
    }
}
