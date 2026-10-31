namespace SiteLedger.Core;

public enum Plan { Free, Pro, Enterprise }
public enum ApprovalStatus { Draft, Submitted, Approved, Rejected }
public enum ProjectStatus { Planning, Active, OnHold, Completed }
public enum TaskStatus { Todo, InProgress, Blocked, Done }
public enum TaskPriority { Low, Medium, High, Critical }
public enum ExpenseCategory { Materials, Labour, Equipment, Transport, Other }
public enum FileCategory { Photo, Drawing, Invoice, Contract, Report }
public enum SubscriptionStatus { Active, Suspended }
public static class Roles
{
    public const string SuperAdmin = nameof(SuperAdmin), TenantAdmin = nameof(TenantAdmin),
        Manager = nameof(Manager), Member = nameof(Member), Viewer = nameof(Viewer);
}

public record PlanLimits(int? ActiveProjects, int Staff, int? Viewers, long StorageBytes, long MaxFileBytes, int AuditRetentionDays, bool PdfReports, bool CsvExport, bool ApiKeys)
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    public static PlanLimits For(Plan plan) => plan switch
    {
        Plan.Free => new(2, 3, 2, 500 * MiB, 5 * MiB, 30, false, false, false),
        Plan.Pro => new(15, 15, 25, 20 * GiB, 25 * MiB, 365, true, true, false),
        Plan.Enterprise => new(null, 100, null, 200 * GiB, 100 * MiB, 1825, true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(plan))
    };
}

public abstract class TenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CompanyName { get; set; } = "";
    public string Slug { get; set; } = "";
    public Plan Plan { get; set; }
    public SubscriptionStatus SubscriptionStatus { get; set; }
    public string Currency { get; set; } = "ZAR";
    public long StorageUsedBytes { get; set; }
    public Guid? LogoFileId { get; set; }
}
public class Project : TenantEntity
{
    public string Name { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string SiteAddress { get; set; } = "";
    public decimal Budget { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public ProjectStatus Status { get; set; }
}
public class ProjectMember : TenantEntity { public Guid ProjectId { get; set; } public string UserId { get; set; } = ""; public string RoleOnProject { get; set; } = ""; }
public class ProjectTask : TenantEntity
{
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? AssignedToUserId { get; set; }
    public DateOnly? DueDate { get; set; }
    public TaskPriority Priority { get; set; }
    public TaskStatus Status { get; set; }
    public int PercentComplete { get; set; }
}
public abstract class Approvable : TenantEntity
{
    public Guid ProjectId { get; set; }
    public string CreatedByUserId { get; set; } = "";
    public ApprovalStatus Status { get; private set; }
    public string? ReviewComment { get; private set; }
    public string? ReviewedByUserId { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public void Submit()
    {
        if (Status is not (ApprovalStatus.Draft or ApprovalStatus.Rejected)) throw new InvalidOperationException("Only drafts or rejected items can be submitted.");
        Status = ApprovalStatus.Submitted; ReviewComment = null; ReviewedByUserId = null; ReviewedAt = null;
    }
    public void Review(bool approve, string reviewerId, string? comment, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Submitted) throw new InvalidOperationException("Only submitted items can be reviewed.");
        if (string.IsNullOrWhiteSpace(reviewerId) || string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("A reviewer and comment are required.");
        Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        ReviewedByUserId = reviewerId; ReviewComment = comment.Trim(); ReviewedAt = now;
    }
}
public class SiteReport : Approvable
{
    public DateOnly ReportDate { get; set; }
    public string Weather { get; set; } = "";
    public int WorkersOnSite { get; set; }
    public string WorkSummary { get; set; } = "";
    public string Issues { get; set; } = "";
}
public class Expense : Approvable
{
    public DateOnly ExpenseDate { get; set; }
    public ExpenseCategory Category { get; set; }
    public decimal Amount { get; set; }
    public string Supplier { get; set; } = "";
}
public class FileAsset : TenantEntity
{
    public Guid ProjectId { get; set; }
    public Guid? SiteReportId { get; set; }
    public Guid? ExpenseId { get; set; }
    public string BlobPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public FileCategory Category { get; set; }
    public string UploadedBy { get; set; } = "";
    public bool IsSharedWithClient { get; set; }
}
public class Comment : TenantEntity { public string EntityType { get; set; } = ""; public Guid EntityId { get; set; } public string Text { get; set; } = ""; public string AuthorId { get; set; } = ""; }
public class Invitation : TenantEntity { public string Email { get; set; } = ""; public string Role { get; set; } = ""; public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset? AcceptedAt { get; set; } }
public class RefreshToken : TenantEntity { public string UserId { get; set; } = ""; public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset? RevokedAt { get; set; } public string? ReplacedByHash { get; set; } public Guid FamilyId { get; set; } }
public class AuditLog : TenantEntity { public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; public Guid EntityId { get; set; } public string UserId { get; set; } = ""; public string? IpAddress { get; set; } public DateTimeOffset Timestamp { get; set; } public string Details { get; set; } = "{}"; }
