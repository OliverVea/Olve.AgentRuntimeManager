import { join } from "node:path";
import { implement } from "@olivervea/pipelines";
import { claudeCode } from "../pipeline";
import fetchClaude from "../scripts/claude-code.sh" with { type: "file" };

export default implement(claudeCode, async (_input, ctx) => {
  const out = join(ctx.tmpDir, "claude-code");
  await ctx.sh({ script: fetchClaude }, { env: { OUT: out } });
  const version = (await ctx.readText(join(out, "claude-version.txt"))).trim();
  return { version, binary: await ctx.output.file(join(out, "claude"), { name: "claude" }) };
});
