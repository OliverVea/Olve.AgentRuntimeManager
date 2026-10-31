import { implement, step, Source } from "@olivervea/pipelines";
import { installMise, miseRun } from "../lib/mise";

export const test = implement(step({
  name: "test",
  description: "Runs `mise run ci`.",
  image: "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  input: { source: Source },
  timeoutSeconds: 1800,
}), async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  await installMise(ctx, tree);
  await miseRun(ctx, tree, "ci");
});
