using SiteLedger.Core;

namespace SiteLedger.Tests;

public class DomainTests
{
    [Theory]
    [InlineData(Plan.Free, 2, 3, 2, 500L * 1024 * 1024, 5L * 1024 * 1024)]
    [InlineData(Plan.Pro, 15, 15, 25, 20L * 1024 * 1024 * 1024, 25L * 1024 * 1024)]
    public void PublishedLimitsAreRepresented(Plan plan, int projects, int staff, int viewers, long storage, long file)
    {
        var limits = PlanLimits.For(plan);
        Assert.Equal(projects, limits.ActiveProjects); Assert.Equal(staff, limits.Staff);
        Assert.Equal(viewers, limits.Viewers); Assert.Equal(storage, limits.StorageBytes); Assert.Equal(file, limits.MaxFileBytes);
    }
    [Fact]
    public void EnterpriseHasUnlimitedProjectsAndViewers()
    {
        var limits = PlanLimits.For(Plan.Enterprise);
        Assert.Null(limits.ActiveProjects); Assert.Null(limits.Viewers);
        Assert.Equal(100, limits.Staff); Assert.True(limits.ApiKeys);
    }
    [Theory]
    [InlineData(Roles.SuperAdmin, Permissions.ManageTenants, true)]
    [InlineData(Roles.SuperAdmin, Permissions.ReadProjects, false)]
    [InlineData(Roles.Viewer, Permissions.ReadProjects, true)]
    [InlineData(Roles.Viewer, Permissions.UploadFiles, false)]
    [InlineData(Roles.Member, Permissions.Review, false)]
    [InlineData(Roles.Manager, Permissions.Review, true)]
    [InlineData(Roles.TenantAdmin, Permissions.ManageUsers, true)]
    public void PermissionsFollowRoleMatrix(string role, string permission, bool expected)
        => Assert.Equal(expected, Permissions.Allows(role, permission));
    [Fact]
    public void FreePlanEnforcesExactProjectUserAndStorageBoundaries()
    {
        PlanRules.EnsureProjectCapacity(Plan.Free, 1);
        Assert.Throws<PlanLimitException>(() => PlanRules.EnsureProjectCapacity(Plan.Free, 2));
        Assert.Throws<PlanLimitException>(() => PlanRules.EnsureUserCapacity(Plan.Free, false, 3, 0));
        Assert.Throws<PlanLimitException>(() => PlanRules.EnsureUserCapacity(Plan.Free, true, 0, 2));
        var limits = PlanLimits.For(Plan.Free);
        PlanRules.EnsureUploadCapacity(Plan.Free, limits.StorageBytes - 1, 1);
        Assert.Throws<PlanLimitException>(() => PlanRules.EnsureUploadCapacity(Plan.Free, limits.StorageBytes, 1));
        Assert.Throws<PlanLimitException>(() => PlanRules.EnsureUploadCapacity(Plan.Free, 0, limits.MaxFileBytes + 1));
    }
    [Fact]
    public void ViewerNeedsMembershipAndApprovedOrSharedContent()
    {
        Assert.False(AccessRules.CanReadProject(Roles.Viewer, false));
        Assert.False(AccessRules.CanReadReport(Roles.Viewer, true, ApprovalStatus.Submitted));
        Assert.True(AccessRules.CanReadReport(Roles.Viewer, true, ApprovalStatus.Approved));
        Assert.False(AccessRules.CanReadFile(Roles.Viewer, true, false));
        Assert.True(AccessRules.CanReadFile(Roles.Viewer, true, true));
        Assert.False(AccessRules.CanReadFile(Roles.Viewer, false, true));
        Assert.False(AccessRules.CanReadFile(Roles.SuperAdmin, true, true));
    }
    [Fact]
    public void MemberCanOnlyEditOwnDraftOrRejectedSubmission()
    {
        Assert.True(AccessRules.CanEditOwnSubmission(Roles.Member, true, true, ApprovalStatus.Draft));
        Assert.True(AccessRules.CanEditOwnSubmission(Roles.Member, true, true, ApprovalStatus.Rejected));
        Assert.False(AccessRules.CanEditOwnSubmission(Roles.Member, true, false, ApprovalStatus.Draft));
        Assert.False(AccessRules.CanEditOwnSubmission(Roles.Member, true, true, ApprovalStatus.Approved));
    }
    [Fact]
    public void ReviewRequiresSubmissionAndComment()
    {
        var report = new SiteReport();
        Assert.Throws<InvalidOperationException>(() => report.Review(true, "manager", "ok", DateTimeOffset.UtcNow));
        report.Submit();
        Assert.Throws<ArgumentException>(() => report.Review(false, "manager", " ", DateTimeOffset.UtcNow));
        report.Review(false, "manager", "Incomplete", DateTimeOffset.UtcNow);
        Assert.Equal(ApprovalStatus.Rejected, report.Status);
        report.Submit();
        Assert.Null(report.ReviewComment);
        Assert.Equal(ApprovalStatus.Submitted, report.Status);
    }
}
