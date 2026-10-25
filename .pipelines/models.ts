import { fileRef, model, z } from "@olivervea/pipelines";

export const DeployRequest = model("DeployRequest", z.object({
  repo: z.string().default("OliverVea/Olve.AgentRuntimeManager"),
  sha: z.string().regex(/^[0-9a-f]{40}$/),
  deployTo: z.enum(["beta", "prod"]).default("prod"),
}));

/** The latest Claude Code, verified against its signed release manifest. */
export const ClaudeCode = model("ClaudeCode", z.object({ version: z.string(), binary: fileRef() }));

/** A release installed in an ARM VM. */
export const VmRelease = model("VmRelease", z.object({ env: z.enum(["beta", "prod"]), version: z.string(), url: z.string() }));
