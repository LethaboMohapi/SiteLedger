using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SiteLedger.Api.Auth;
using SiteLedger.Infrastructure;
using Testcontainers.MySql;

namespace SiteLedger.Tests;

public sealed class EmailOutboxTests
{
    private sealed record NoTenant : ICurrentTenant { public Guid? TenantId => null; public string? UserId => null; }
    private sealed class Transport : IAccountEmailTransport
    {
        public bool IsConfigured => true;
        public int Attempts { get; private set; }
        public bool Fail { get; set; }
        public PendingEmail? Last { get; private set; }
        public Task SendAsync(string address, string subject, string body, CancellationToken ct)
        {
            Attempts++;
            if (Fail) throw new IOException("temporary SMTP failure");
            Last = new(address, subject, body);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingTransport : IAccountEmailTransport
    {
        public bool IsConfigured => true;
        public int Attempts;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(string address, string subject, string body, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task EncryptedMessageRetriesAfterFailureAndIsScrubbedAfterDelivery()
    {
        await using var mysql = new MySqlBuilder().WithImage("mysql:8").Build();
        await mysql.StartAsync();
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>().UseMySql(mysql.GetConnectionString(), new MySqlServerVersion(new Version(8, 0, 36))).Options;
        await using var db = new SiteLedgerDbContext(options, new NoTenant());
        await db.Database.MigrateAsync();
        var protection = new EphemeralDataProtectionProvider();
        var transport = new Transport { Fail = true };
        var sender = new OutboxAccountEmailSender(db, protection, transport);
        await sender.SendAsync("user@example.test", "Confirm", "secret-token-123", CancellationToken.None);
        var message = await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(message.ProtectedPayload);
        Assert.DoesNotContain("secret-token-123", message.ProtectedPayload);
        Assert.DoesNotContain("user@example.test", message.ProtectedPayload);
        var worker = new EmailOutboxDispatcher.EmailOutboxWorker(db, protection, transport, NullLogger.Instance);
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        message = await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.DeliveredAt);
        Assert.Null(message.LeaseId);
        Assert.True(message.NextAttemptAt > DateTimeOffset.UtcNow);
        await db.EmailOutbox.ExecuteUpdateAsync(set => set.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        transport.Fail = false;
        Assert.True(await worker.ProcessOneAsync(CancellationToken.None));
        message = await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(2, message.Attempts);
        Assert.NotNull(message.DeliveredAt);
        Assert.Null(message.ProtectedPayload);
        Assert.Equal("secret-token-123", transport.Last?.Body);
        Assert.False(await worker.ProcessOneAsync(CancellationToken.None));

        var blocking = new BlockingTransport();
        await sender.SendAsync("another@example.test", "Confirm", "another-secret", CancellationToken.None);
        await using var db2 = new SiteLedgerDbContext(options, new NoTenant());
        var first = new EmailOutboxDispatcher.EmailOutboxWorker(db, protection, blocking, NullLogger.Instance);
        var second = new EmailOutboxDispatcher.EmailOutboxWorker(db2, protection, blocking, NullLogger.Instance);
        var running = first.ProcessOneAsync(CancellationToken.None);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await second.ProcessOneAsync(CancellationToken.None));
        blocking.Release.TrySetResult();
        Assert.True(await running);
        Assert.Equal(1, blocking.Attempts);
    }
}
