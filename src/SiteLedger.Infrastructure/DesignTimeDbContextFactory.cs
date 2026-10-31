using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SiteLedger.Infrastructure;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SiteLedgerDbContext>
{
    public SiteLedgerDbContext CreateDbContext(string[] args)
    {
        // Building a migration does not open a database connection.
        var connection = Environment.GetEnvironmentVariable("SITELEDGER_DATABASE") ?? "Server=localhost;Database=siteledger";
        var options = new DbContextOptionsBuilder<SiteLedgerDbContext>()
            .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 36))).Options;
        return new SiteLedgerDbContext(options, new DesignTenant());
    }

    private sealed class DesignTenant : ICurrentTenant
    {
        public Guid? TenantId => null;
        public string? UserId => null;
    }
}
