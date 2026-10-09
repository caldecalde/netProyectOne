using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CareFlow.Ingestion.IntegrationTests.Infrastructure;

/// <summary>
/// Authenticates from test headers and emits a Keycloak-shaped <c>realm_access</c> claim,
/// so the production role mapping and authorization policies run unchanged.
/// </summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var subject) || string.IsNullOrEmpty(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var roles = Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", subject.ToString()),
                new Claim("preferred_username", subject.ToString()),
                new Claim("realm_access", JsonSerializer.Serialize(new { roles })),
            ],
            SchemeName,
            nameType: "preferred_username",
            roleType: "roles");

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
