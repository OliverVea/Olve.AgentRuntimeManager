import { implement } from "@olivervea/pipelines";
import { test } from "../pipeline";
import { installMise, miseRun } from "./lib/mise";

export default implement(test, async ({ source }, ctx) => {
  const tree = await ctx.files.fetch(source.tree);
  await installMise(ctx, tree);
  await miseRun(ctx, tree, "ci");
});
