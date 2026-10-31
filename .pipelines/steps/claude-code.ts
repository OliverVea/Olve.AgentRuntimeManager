import { rmSync } from "node:fs";
import { join } from "node:path";
import { implement, step } from "@olivervea/pipelines";
import * as M from "../models";
import fetchClaude from "../scripts/claude-code.sh" with { type: "file" };

export const claudeCode = implement(step({
  name: "claude-code",
  description: "Fetches the latest Claude Code, verifies its signed manifest and checksum, and checks it has the flags ARM's lockdown needs.",
  image: "debian:stable-slim",
  output: M.ClaudeCode,
  timeoutSeconds: 600,
}), async (_input, ctx) => {
  const out = join(ctx.tmpDir, "claude-code");
  await ctx.sh({ script: fetchClaude }, { env: { OUT: out } });
  const version = (await ctx.readText(join(out, "claude-version.txt"))).trim();
  const binary = await ctx.output.file(join(out, "claude"), { name: "claude" });
  // The binary is near a third of the scratch space, and packing the result copies it again.
  rmSync(out, { recursive: true });
  return { version, binary };
});
