import { secret } from "@olivervea/pipelines";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const CLAUDE_CODE_OAUTH_TOKEN_BETA = secret("CLAUDE_CODE_OAUTH_TOKEN_BETA", "Claude Code OAuth token (`claude setup-token`) for the beta VM's agents. Use sparingly.");
export const CLAUDE_CODE_OAUTH_TOKEN_PROD = secret("CLAUDE_CODE_OAUTH_TOKEN_PROD", "Claude Code OAuth token (`claude setup-token`) for the prod VM's agents.");
export const ARM_BETA_OIDC_CLIENT_SECRET = secret("ARM_BETA_OIDC_CLIENT_SECRET", "Secret of beta's `olve-arm` confidential client: beta-test's token for its sessions.");
// kaniko under gVisor stalls pulling from these registries directly.
export const REGISTRY_MAP = { "mcr.microsoft.com": "mcr-cache.ovea.pro", "index.docker.io": "hub-cache.ovea.pro" };
