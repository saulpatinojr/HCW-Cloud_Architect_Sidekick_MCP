namespace AdrMcp.Domain.Tenancy;

public enum Role { Reader, Author, Approver, Admin }

public enum Permission
{
    AdrRead, AdrCreate, AdrEdit, AdrPropose, AdrApproveChange, AdrTransitionStatus, AdrSupersede, AdrArchive,
    EvidenceUpload, EvidenceRemove, AnalysisRun, FindingReview, RuleManage, JobRead, JobCancel, MemoryRead
}

/// <summary>The authenticated caller, resolved server-side. Never populated from tool arguments.</summary>
public sealed record TenantContext(
    string TenantId,
    string PrincipalId,
    string PrincipalDisplay,
    IReadOnlySet<Role> Roles,
    IReadOnlySet<string> AllowedWorkspaceIds,
    string? DefaultWorkspaceId,
    string AuthenticationMode,
    string CorrelationId,
    string? SessionId = null)
{
    public bool AllWorkspaces => AllowedWorkspaceIds.Contains("*");
    public bool CanAccessWorkspace(string workspaceId) => AllWorkspaces || AllowedWorkspaceIds.Contains(workspaceId);
}

public static class RolePermissions
{
    private static readonly Permission[] ReaderPerms = { Permission.AdrRead, Permission.JobRead, Permission.MemoryRead };
    private static readonly Permission[] AuthorPerms = ReaderPerms.Concat(new[] { Permission.AdrCreate, Permission.AdrEdit, Permission.AdrPropose, Permission.EvidenceUpload, Permission.AnalysisRun, Permission.JobCancel }).ToArray();
    private static readonly Permission[] ApproverPerms = AuthorPerms.Concat(new[] { Permission.AdrApproveChange, Permission.AdrTransitionStatus, Permission.AdrSupersede, Permission.AdrArchive, Permission.FindingReview, Permission.EvidenceRemove }).ToArray();
    private static readonly Permission[] AdminPerms = Enum.GetValues<Permission>();

    public static bool Has(IReadOnlySet<Role> roles, Permission p)
        => roles.Any(r => r switch
        {
            Role.Reader => ReaderPerms.Contains(p),
            Role.Author => AuthorPerms.Contains(p),
            Role.Approver => ApproverPerms.Contains(p),
            Role.Admin => AdminPerms.Contains(p),
            _ => false
        });
}
