namespace SiteLedger.Core;

// These rules are only useful when the calling service obtains all inputs from
// authenticated claims and database records. They are not an authorization boundary by themselves.
public static class AccessRules
{
    public static bool CanReadProject(string role, bool isProjectMember) => role switch
    {
        Roles.TenantAdmin => true,
        Roles.Manager or Roles.Member or Roles.Viewer => isProjectMember,
        _ => false
    };

    public static bool CanReadReport(string role, bool isProjectMember, ApprovalStatus status) =>
        CanReadProject(role, isProjectMember) && (role != Roles.Viewer || status == ApprovalStatus.Approved);

    public static bool CanReadFile(string role, bool isProjectMember, bool isSharedWithClient) =>
        CanReadProject(role, isProjectMember) && (role != Roles.Viewer || isSharedWithClient);

    public static bool CanEditOwnSubmission(string role, bool isProjectMember, bool isOwner, ApprovalStatus status) =>
        role == Roles.Member && isProjectMember && isOwner && status is ApprovalStatus.Draft or ApprovalStatus.Rejected;
}
