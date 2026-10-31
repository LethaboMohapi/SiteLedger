using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;
using SiteLedger.Infrastructure;

namespace SiteLedger.Tests;

public class TenantPersistenceTests
{
    private sealed record Tenant(Guid? TenantId, string? UserId) : ICurrentTenant;

    [Fact]
    public void MySqlProviderCanGenerateSchemaForModel()
    {
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>()
            .UseMySql("Server=localhost;Database=siteledger;User=test;Password=test", new MySqlServerVersion(new Version(8, 0, 36))).Options;
        using var db = new SiteLedgerDbContext(options, new Tenant(Guid.NewGuid(), "test-user"));
        var sql = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE", sql);
        Assert.Contains("TenantId", sql);
    }

    [Fact]
    public async Task TenantQueriesAndWritesAreScopedAndDeleteIsSoft()
    {
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var project = new Project { Name = "Tenant A project" };
        await using (var db = new SiteLedgerDbContext(options, new Tenant(a, "a-user")))
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            Assert.Equal(a, project.TenantId);
            Assert.Equal("a-user", project.CreatedBy);
        }
        await using (var db = new SiteLedgerDbContext(options, new Tenant(b, "b-user")))
        {
            Assert.Empty(await db.Projects.ToListAsync());
            db.Projects.Add(new Project { TenantId = a, Name = "Cross tenant" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = new SiteLedgerDbContext(options, new Tenant(a, "a-user")))
        {
            var item = await db.Projects.SingleAsync();
            db.Remove(item);
            await db.SaveChangesAsync();
            Assert.Empty(await db.Projects.ToListAsync());
            Assert.True((await db.Projects.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        }
        await using var anonymous = new SiteLedgerDbContext(options, new Tenant(null, null));
        Assert.Empty(await anonymous.Projects.ToListAsync());
    }
}
