using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;
using Testcontainers.MySql;

namespace SiteLedger.Tests;

public sealed class MySqlTenantIsolationTests
{
    private sealed record Tenant(Guid? TenantId, string? UserId) : ICurrentTenant;

    [Fact]
    public async Task GuessedIdentifiersCannotReadChangeOrDeleteAnotherTenantsProject()
    {
        await using var container = new MySqlBuilder().WithImage("mysql:8").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>()
            .UseMySql(container.GetConnectionString(), new MySqlServerVersion(new Version(8, 0, 36))).Options;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var item = new Project { Name = "A project", ClientName = "A", SiteAddress = "A" };
        await using (var a = new SiteLedgerDbContext(options, new Tenant(tenantA, "user-a")))
        {
            await a.Database.MigrateAsync();
            a.Projects.Add(item);
            await a.SaveChangesAsync();
        }
        await using (var b = new SiteLedgerDbContext(options, new Tenant(tenantB, "user-b")))
        {
            Assert.Null(await b.Projects.SingleOrDefaultAsync(p => p.Id == item.Id));
            var forged = new Project { Id = item.Id, TenantId = tenantB, RowVersion = item.RowVersion, Name = "Changed" };
            b.Projects.Update(forged);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
        await using (var b = new SiteLedgerDbContext(options, new Tenant(tenantB, "user-b")))
        {
            var forged = new Project { Id = item.Id, TenantId = tenantB, RowVersion = item.RowVersion };
            b.Projects.Remove(forged);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        }
        await using (var a = new SiteLedgerDbContext(options, new Tenant(tenantA, "user-a")))
            Assert.Equal("A project", (await a.Projects.SingleAsync()).Name);
    }
}
