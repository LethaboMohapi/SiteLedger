using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SiteLedger.Core;

namespace SiteLedger.Infrastructure;

public class ApplicationUser : IdentityUser
{
    public Guid? TenantId { get; set; }
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class SiteLedgerDbContext(DbContextOptions<SiteLedgerDbContext> options, ICurrentTenant current)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<SiteReport> SiteReports => Set<SiteReport>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        builder.Entity<Tenant>().Property(x => x.CompanyName).HasMaxLength(200);
        builder.Entity<Tenant>().Property(x => x.Slug).HasMaxLength(100);
        builder.Entity<TenantEntity>().UseTpcMappingStrategy();
        builder.Entity<TenantEntity>().HasKey(x => new { x.TenantId, x.Id });
        builder.Entity<TenantEntity>().Property(x => x.RowVersion).IsConcurrencyToken();
        builder.Entity<TenantEntity>().HasQueryFilter(x => current.TenantId != null && x.TenantId == current.TenantId && !x.IsDeleted);
        builder.Entity<Project>().Property(x => x.Name).HasMaxLength(200);
        builder.Entity<Project>().Property(x => x.Budget).HasPrecision(18, 2);
        builder.Entity<Expense>().Property(x => x.Amount).HasPrecision(18, 2);
        builder.Entity<Project>().HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        builder.Entity<ProjectMember>().HasIndex(x => new { x.TenantId, x.ProjectId, x.UserId }).IsUnique();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampChanges()
    {
        var entries = ChangeTracker.Entries<TenantEntity>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        if (entries.Length == 0) return;
        var tenantId = current.TenantId ?? throw new InvalidOperationException("A tenant claim is required for tenant data changes.");
        var userId = current.UserId ?? throw new InvalidOperationException("An authenticated user is required for tenant data changes.");
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != tenantId) throw new InvalidOperationException("Cross-tenant write rejected.");
                entry.Entity.TenantId = tenantId;
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedBy = userId;
            }
            else
            {
                if (entry.Entity.TenantId != tenantId || (Guid)entry.Property(x => x.TenantId).OriginalValue != tenantId) throw new InvalidOperationException("Cross-tenant write rejected.");
                if (entry.State == EntityState.Deleted) { entry.State = EntityState.Modified; entry.Entity.IsDeleted = true; }
                entry.Property(x => x.CreatedAt).IsModified = false;
                entry.Property(x => x.CreatedBy).IsModified = false;
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy = userId;
            }
            entry.Entity.RowVersion = Guid.NewGuid();
        }
    }
}
