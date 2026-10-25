import type { Ctx } from "@olivervea/pipelines";

// Leftover MSBuild nodes and the compiler server would hold the step's output pipe open after dotnet exits.
const ENV = { MSBUILDDISABLENODEREUSE: "1", UseSharedCompilation: "false" };

export async function installMise(ctx: Ctx, tree: string) {
  await ctx.sh("curl -fsSL https://mise.run | MISE_INSTALL_PATH=/usr/local/bin/mise sh");
  await ctx.exec(["mise", "trust", "--yes"], { cwd: tree });
  await ctx.exec(["mise", "install"], { cwd: tree });
}

export async function miseRun(ctx: Ctx, tree: string, task: string, env: Record<string, string> = {}) {
  await ctx.exec(["mise", "run", task], { cwd: tree, env: { ...ENV, ...env } });
}
