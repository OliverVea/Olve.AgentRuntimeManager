// Every push goes beta, then prod.
import { pipeline, steps } from "@olivervea/pipelines";
import * as M from "./models";
import { CLAUDE_CODE_OAUTH_TOKEN_BETA, CLAUDE_CODE_OAUTH_TOKEN_PROD, GITHUB_TOKEN, REGISTRY_MAP } from "./shared";
import { betaTest } from "./steps/beta-test";
import { claudeCode } from "./steps/claude-code";
import { test } from "./steps/test";
import { vmDeploy } from "./vm-deploy";

export { SSH_PRIVATE_KEY } from "./vm-deploy";
import { SSH_PRIVATE_KEY } from "./vm-deploy";

const source = steps.gitHubSource({
  name: "source",
  description: "Fetches the pushed commit from GitHub.",
  token: GITHUB_TOKEN,
});

const build = steps.imageBuild({
  name: "build",
  description: "Builds the ARM image with kaniko; the VM deploy extracts /app from it.",
  repository: "olve-arm",
  registryMap: REGISTRY_MAP,
  timeoutSeconds: 1800,
});

const deployBeta = vmDeploy({
  name: "deploy-beta",
  description: "Installs the release and Claude Code in the beta VM, then checks arm-beta.ovea.pro/health from the host.",
  env: "beta",
  claudeToken: CLAUDE_CODE_OAUTH_TOKEN_BETA,
  healthUrl: "https://arm-beta.ovea.pro",
});

const deployProd = vmDeploy({
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
