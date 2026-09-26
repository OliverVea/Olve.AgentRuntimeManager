using Olve.AgentRuntimeManager.Api;
using Olve.Results;

namespace Olve.AgentRuntimeManager.Configuration;

/// <summary>
/// <c>GET /api/auth-config</c>: the public OIDC settings the SPA needs to start an Authorization
/// Code + PKCE login. Served at runtime (not baked into the bundle) because one image deploys to
/// multiple Authentik environments — beta validates against auth-beta, prod against auth.
/// Everything here is public: a browser (public PKCE client) holds no secret.
/// </summary>
public sealed class GetAuthConfigHandler(IConfiguration config) : IAuthConfigGetHandler
{
    public Task<Result<FrontendAuthConfig>> HandleAsync(AuthConfigGetRequest request, CancellationToken cancellationToken)
    {
        // Login runs against the SPA's own public provider (separate client from the confidential
        // one used for machine tokens). Fall back to the resource authority if no dedicated
        // frontend provider is configured (e.g. local dev). `offline_access` yields a refresh token.
        return Task.FromResult<Result<FrontendAuthConfig>>(new FrontendAuthConfig
        {
            Authority = config["Auth:Frontend:Authority"] ?? config["Auth:Authority"],
            ClientId = config["Auth:Frontend:ClientId"],
            Scopes = config["Auth:Frontend:Scopes"] ?? "openid profile email offline_access",
        });
    }
}
