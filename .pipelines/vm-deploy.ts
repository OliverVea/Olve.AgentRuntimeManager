import { implement, secret, sshSession, step, BuiltImage, Source, type LiteralName, type Secret } from "@olivervea/pipelines";
import { ClaudeCode, VmRelease } from "./models";

export const HOST = "oliver@bulwark-m2";
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for the homelab host (VM provisioning and releases).");

interface VmDeployDef<N extends string, T extends string> {
  name: LiteralName<N>;
  description: string;
  env: "beta" | "prod";
  /** The VM's agents' Claude Code OAuth token. */
  claudeToken: Secret<T>;
  /** Checked from the host after the deploy. */
  healthUrl: string;
}

/** Installs a release in the env's VM with src/deploy/vm/vm-deploy.sh on the host, then checks its health from there. */
export function vmDeploy<const N extends string, const T extends string>(def: VmDeployDef<N, T>) {
  const contract = step({
    name: def.name,
    description: def.description,
    kind: "armVmDeploy",
    image: "alpine:3.22",
    input: { source: Source, image: BuiltImage, claude: ClaudeCode },
    output: VmRelease,
    secrets: [SSH_PRIVATE_KEY, def.claudeToken],
    timeoutSeconds: 1800,
    settings: { env: def.env, healthUrl: def.healthUrl },
  });
  return implement(contract, async ({ source, image, claude }, ctx) => {
    const tree = await ctx.files.fetch(source.tree);
    const ssh = await sshSession(ctx, { host: HOST, key: ctx.secrets.SSH_PRIVATE_KEY });
    const remote = `olve-arm-deploy-${def.env}`;
    await ssh.run(`rm -rf ${remote}`);
    await ssh.copy(`${tree}/src/deploy/vm`, remote);
    await ssh.run(`mkdir -p ${remote}/claude-code`);
    await ssh.copy(await ctx.files.fetch(image.tar), `${remote}/image.tar`);
    await ssh.copy(await ctx.files.fetch(claude.binary), `${remote}/claude-code/claude`);
    await ssh.run(`printf '%s\\n' ${claude.version} > ${remote}/claude-code/claude-version.txt`);
    ctx.log("deploying", { env: def.env, version: image.tag, claude: claude.version });
    // The token goes over stdin, never on a command line.
    await ctx.sh(`printf '%s\\n' "$${def.claudeToken.name}" | ${ssh.shell(`cd ${remote} && bash vm-deploy.sh ${def.env} ${image.tag} image.tar claude-code --claude-token-stdin && cd && rm -rf ${remote}`)}`);
    await ctx.retry({ attempts: 5, delaySeconds: 5 }, () => ssh.run(`curl -skf -o /dev/null ${def.healthUrl}/health`));
    ctx.log("healthy", { url: def.healthUrl });
    return { env: def.env, version: image.tag, url: def.healthUrl };
  });
}
