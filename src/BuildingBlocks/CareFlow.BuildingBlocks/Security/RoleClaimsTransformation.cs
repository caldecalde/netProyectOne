using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace CareFlow.BuildingBlocks.Security;

/// <summary>
/// Normalizes identity-provider specific role claims into a single <see cref="CareFlowClaims.Role"/> claim type,
/// so authorization policies work unchanged against Keycloak (local) and Amazon Cognito (AWS).
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    private const string KeycloakRealmAccess = "realm_access";
    private const string CognitoGroups = "cognito:groups";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Task.FromResult(principal);
        }

        var existing = identity.FindAll(CareFlowClaims.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var role in ExtractRoles(identity).Where(existing.Add))
        {
            identity.AddClaim(new Claim(CareFlowClaims.Role, role));
        }

        return Task.FromResult(principal);
    }

    private static List<string> ExtractRoles(ClaimsIdentity identity)
    {
        List<string> roles = [.. identity.FindAll(CognitoGroups).Select(c => c.Value)];

        var realmAccess = identity.FindFirst(KeycloakRealmAccess)?.Value;
        if (string.IsNullOrEmpty(realmAccess))
        {
            return roles;
        }

        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("roles", out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                roles.AddRange(array.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!));
            }
        }
        catch (JsonException)
        {
            // A malformed claim grants nothing.
        }

        return roles;
    }
}
