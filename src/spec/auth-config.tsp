import "@typespec/http";

using Http;

namespace Arm;

/** Public OIDC settings the SPA needs to log in. */
model FrontendAuthConfig {
  authority: string | null;
  clientId: string | null;
  scopes: string;
}

@route("/auth-config")
interface AuthConfig {
  @useAuth(NoAuth)
  @get get(): FrontendAuthConfig;
}
