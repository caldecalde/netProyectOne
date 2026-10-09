namespace CareFlow.BuildingBlocks.Security;

public static class CareFlowClaims
{
    public const string Role = "roles";
    public const string Subject = "sub";
    public const string PreferredUsername = "preferred_username";
}

public static class CareFlowRoles
{
    public const string Submitter = "submitter";
    public const string Viewer = "viewer";
    public const string Auditor = "auditor";
}

public static class CareFlowPolicies
{
    public const string CanSubmit = nameof(CanSubmit);
    public const string CanRead = nameof(CanRead);
}
