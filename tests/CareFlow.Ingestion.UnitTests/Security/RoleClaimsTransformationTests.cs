using System.Security.Claims;
using CareFlow.BuildingBlocks.Security;

namespace CareFlow.Ingestion.UnitTests.Security;

public class RoleClaimsTransformationTests
{
    private readonly RoleClaimsTransformation _sut = new();

    [Fact]
    public async Task KeycloakRealmRoles_AreMappedToRoleClaims()
    {
        var principal = Authenticated(new Claim("realm_access", """{"roles":["submitter","offline_access"]}"""));

        var result = await _sut.TransformAsync(principal);

        Assert.True(result.IsInRole(CareFlowRoles.Submitter));
        Assert.True(result.IsInRole("offline_access"));
    }

    [Fact]
    public async Task CognitoGroups_AreMappedToRoleClaims()
    {
        var principal = Authenticated(new Claim("cognito:groups", "viewer"), new Claim("cognito:groups", "auditor"));

        var result = await _sut.TransformAsync(principal);

        Assert.True(result.IsInRole(CareFlowRoles.Viewer));
        Assert.True(result.IsInRole(CareFlowRoles.Auditor));
    }

    [Fact]
    public async Task RunningTwice_DoesNotDuplicateClaims()
    {
        var principal = Authenticated(new Claim("cognito:groups", "viewer"));

        await _sut.TransformAsync(principal);
        await _sut.TransformAsync(principal);

        Assert.Single(principal.FindAll(CareFlowClaims.Role));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"roles":"submitter"}""")]
    [InlineData("""["submitter"]""")]
    public async Task MalformedRealmAccess_GrantsNothing(string realmAccess)
    {
        var principal = Authenticated(new Claim("realm_access", realmAccess));

        var result = await _sut.TransformAsync(principal);

        Assert.Empty(result.FindAll(CareFlowClaims.Role));
    }

    [Fact]
    public async Task AnonymousPrincipal_IsLeftUntouched()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("cognito:groups", "submitter")]));

        var result = await _sut.TransformAsync(principal);

        Assert.Empty(result.FindAll(CareFlowClaims.Role));
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer", nameType: "preferred_username", roleType: CareFlowClaims.Role));
}
