// Every push goes beta, then prod.
import { pipeline, secret, step, steps, Source } from "@olivervea/pipelines";
import * as M from "./models";
import { vmDeploy } from "./vm-deploy";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const CLAUDE_CODE_OAUTH_TOKEN_BETA = secret("CLAUDE_CODE_OAUTH_TOKEN_BETA", "Claude Code OAuth token (`claude setup-token`) for the beta VM's agents. Use sparingly.");
export const CLAUDE_CODE_OAUTH_TOKEN_PROD = secret("CLAUDE_CODE_OAUTH_TOKEN_PROD", "Claude Code OAuth token (`claude setup-token`) for the prod VM's agents.");
export const ARM_BETA_OIDC_CLIENT_SECRET = secret("ARM_BETA_OIDC_CLIENT_SECRET", "Secret of beta's `olve-arm` confidential client: beta-test's token for its sessions.");
export { SSH_PRIVATE_KEY } from "./vm-deploy";
import { SSH_PRIVATE_KEY } from "./vm-deploy";

export const source = steps.gitHubSource({
  name: "source",
  description: "Fetches the pushed commit from GitHub.",
  token: GITHUB_TOKEN,
});

export const build = steps.imageBuild({
  name: "build",
  description: "Builds the ARM image with kaniko; the VM deploy extracts /app from it.",
  repository: "olve-arm",
  timeoutSeconds: 1800,
});

export const test = step({
  name: "test",
  description: "Runs `mise run ci`.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  timeoutSeconds: 1800,
});

export const claudeCode = step({
  name: "claude-code",
  description: "Fetches the latest Claude Code, verifies its signed manifest and checksum, and checks it has the flags ARM's lockdown needs.",
  image: "debian:stable-slim",
  output: M.ClaudeCode,
  timeoutSeconds: 600,
});

export const deployBeta = vmDeploy({
  name: "deploy-beta",
  description: "Installs the release and Claude Code in the beta VM, then checks arm-beta.ovea.pro/health from the host.",
  env: "beta",
  claudeToken: CLAUDE_CODE_OAUTH_TOKEN_BETA,
  healthUrl: "https://arm-beta.ovea.pro",
});

export const betaTest = step({
  name: "beta-test",
  description: "Runs the remote API tests (`mise run api:remote`) against beta through the host relay, then the beta checks: real sessions, one Haiku turn.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source, release: M.VmRelease },
  secrets: [ARM_BETA_OIDC_CLIENT_SECRET, SSH_PRIVATE_KEY],
  timeoutSeconds: 1200,
});

export const deployProd = vmDeploy({
  name: "deploy-prod",
  description: "Installs the release and Claude Code in the prod VM, then checks arm-private.ovea.pro/health from the host.",
  env: "prod",
  claudeToken: CLAUDE_CODE_OAUTH_TOKEN_PROD,
  healthUrl: "https://arm-private.ovea.pro",
});

export default pipeline({
  name: "Olve.AgentRuntimeManager",
  description: "Build the image, run the checks and fetch Claude Code, deploy to the beta VM and test it with real sessions, then deploy the same release to the prod VM. deployTo=beta stops after beta.",
  input: M.DeployRequest,
})
  .onPush(e => ({ repo: e.repository.full_name, sha: e.after, deployTo: "prod" }))
  .stage([source, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test, claudeCode)
  .stage(deployBeta)
  .stage(betaTest)
  .when({ deployTo: "prod" }, p => p.stage(deployProd));
