namespace Olve.AgentRuntimeManager.Configuration;

/// <summary>
/// The public OIDC settings the SPA needs to start an Authorization Code + PKCE login. Served at
/// runtime (not baked into the bundle) because one image deploys to multiple Authentik
/// environments — beta validates against auth-beta, prod against auth. Everything here is public:
/// a browser (public PKCE client) holds no secret.
/// </summary>
/// <param name="Authority">OIDC issuer of the SPA's public client — the FE fetches
/// <c>{authority}/.well-known/openid-configuration</c> to discover the authorize/token endpoints.</param>
/// <param name="ClientId">The public client id the browser authorizes as.</param>
/// <param name="Scopes">Space-delimited scopes; <c>offline_access</c> is what yields a refresh token.</param>
public record FrontendAuthConfig(string? Authority, string? ClientId, string Scopes);

public static class FrontendConfigEndpoints
{
    public static void MapFrontendConfig(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth-config", (IConfiguration config) =>
            {
                // Login runs against the SPA's own public provider (separate client from the
                // confidential one used for machine tokens). Fall back to the resource authority
                // if no dedicated frontend provider is configured (e.g. local dev).
                var authority = config["Auth:Frontend:Authority"] ?? config["Auth:Authority"];
                var clientId = config["Auth:Frontend:ClientId"];
                var scopes = config["Auth:Frontend:Scopes"] ?? "openid profile email offline_access";
                return TypedResults.Ok(new FrontendAuthConfig(authority, clientId, scopes));
            })
            .AllowAnonymous()
            .WithName("GetAuthConfig");
    }
}
