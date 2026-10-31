namespace SiteLedger.Core;

public static class Permissions
{
    public const string ManageTenants = nameof(ManageTenants), ManageUsers = nameof(ManageUsers),
        ManageProjects = nameof(ManageProjects), Review = nameof(Review),
        CreateReport = nameof(CreateReport), UploadFiles = nameof(UploadFiles),
        ReadProjects = nameof(ReadProjects), ReadAudit = nameof(ReadAudit),
        ShareFiles = nameof(ShareFiles), Export = nameof(Export), ManageApiKeys = nameof(ManageApiKeys);

    private static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
    {
        [ManageTenants] = [Roles.SuperAdmin],
        [ManageUsers] = [Roles.TenantAdmin],
        [ManageProjects] = [Roles.TenantAdmin, Roles.Manager],
        [Review] = [Roles.TenantAdmin, Roles.Manager],
        [CreateReport] = [Roles.TenantAdmin, Roles.Manager, Roles.Member],
        [UploadFiles] = [Roles.TenantAdmin, Roles.Manager, Roles.Member],
        [ReadProjects] = [Roles.TenantAdmin, Roles.Manager, Roles.Member, Roles.Viewer],
        [ReadAudit] = [Roles.TenantAdmin],
        [ShareFiles] = [Roles.TenantAdmin, Roles.Manager],
        [Export] = [Roles.TenantAdmin, Roles.Manager],
        [ManageApiKeys] = [Roles.TenantAdmin]
    };

    public static bool Allows(string role, string permission) => Matrix.TryGetValue(permission, out var roles) && roles.Contains(role, StringComparer.Ordinal);
    public static IReadOnlyCollection<string> For(string permission) => Matrix.TryGetValue(permission, out var roles) ? Array.AsReadOnly(roles) : Array.Empty<string>();
    public static IEnumerable<string> All => Matrix.Keys;
}
