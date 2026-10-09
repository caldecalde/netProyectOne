using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.BuildingBlocks.Security;

public sealed class JwtAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC issuer, e.g. <c>http://localhost:8080/realms/careflow</c> or the Cognito user pool URL.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Discovery URL, when the API reaches the IdP on a different host than clients do (Docker networks).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Expected <c>aud</c>. Leave empty for IdPs that omit it from access tokens (Cognito).</summary>
    public string? Audience { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthenticationExtensions
{
    public static IServiceCollection AddCareFlowAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(JwtAuthOptions.SectionName).Get<JwtAuthOptions>() ?? new JwtAuthOptions();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                {
                    jwt.MetadataAddress = options.MetadataAddress;
                }

                // Keep the raw OIDC claim names (sub, realm_access, cognito:groups).
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = CareFlowClaims.PreferredUsername;
                jwt.TokenValidationParameters.RoleClaimType = CareFlowClaims.Role;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience);
                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            });

        services.AddTransient<IClaimsTransformation, RoleClaimsTransformation>();

        services.AddAuthorizationBuilder()
            .AddPolicy(CareFlowPolicies.CanSubmit, p => p.RequireRole(CareFlowRoles.Submitter))
            .AddPolicy(CareFlowPolicies.CanRead, p => p.RequireRole(CareFlowRoles.Submitter, CareFlowRoles.Viewer, CareFlowRoles.Auditor));

        return services;
    }
}
