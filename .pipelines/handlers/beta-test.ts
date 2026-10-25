import { join } from "node:path";
import { implement, sshSession } from "@olivervea/pipelines";
import { betaTest } from "../pipeline";
import { HOST } from "../vm-deploy";
import { installMise, miseRun } from "./lib/mise";

// The host relay to the beta VM (pods can't open connections into libvirt's NAT network).
const API = "http://100.100.117.17:18792";

export default implement(betaTest, async ({ source, release }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  await installMise(ctx, tree);
  await ctx.retry({ attempts: 30, delaySeconds: 2 }, () => ctx.http.expectOk(`${API}/health`));
  await miseRun(ctx, tree, "api:remote", { ARM_API_BASE_URL: API });

  // Beta's Authentik serves a certificate from its own CA.
  const ssh = await sshSession(ctx, { host: HOST, key: ctx.secrets.SSH_PRIVATE_KEY });
  const ca = await ssh.text(`kubectl --request-timeout=20s -n apps-beta get configmap authentik-ca -o jsonpath='{.data.auth-beta-ca\\.crt}'`);
  if (!ca.includes("BEGIN CERTIFICATE")) throw new Error("can't read beta's Authentik CA (apps-beta/authentik-ca, key auth-beta-ca.crt)");
  const caFile = join(ctx.tmpDir, "authentik-beta-ca.crt");
  await Bun.write(caFile, ca + "\n");

  const python = await ctx.sh.text("mise which python3", { cwd: tree });
  await ctx.exec([python, "-u", ".pipelines/scripts/beta-check.py"], {
    cwd: tree,
    env: {
      BETA_CHECK_API: API,
      BETA_CHECK_VERSION: release.version,
      BETA_CHECK_AUTHORITY: "https://auth-beta.ovea.pro/application/o/olve-arm/",
      BETA_CHECK_CLIENT_ID: "olve-arm",
      BETA_CHECK_CLIENT_SECRET: ctx.secrets.ARM_BETA_OIDC_CLIENT_SECRET,
      BETA_CHECK_CA_FILE: caFile,
      BETA_CHECK_REPO_URL: "git@github.com:OliverVea/Olve.AgentRuntimeManager.git",
    },
    idleTimeoutSeconds: 300,
  });
});
