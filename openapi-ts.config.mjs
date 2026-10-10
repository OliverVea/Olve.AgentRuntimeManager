// Hey API client generation: artifacts/spec/openapi.json → artifacts/clients/ts (gitignored).
// baseUrl: false — no baked-in default; callers configure the client (same-origin in the SPA,
// --url / ARM_URL in the CLI).
export default {
  input: "artifacts/spec/openapi.json",
  // tsConfigPath null: no search for a tsconfig.json up from Hey API's own folder (there is none
  // in this repo, so it found none anyway). The search lists every parent folder, and fails where
  // one can't be listed: an agent's workplace on the VMs is under /var/lib/olve-arm, which is 0711
  // (docs/AGENT-USER.md).
  output: { path: "artifacts/clients/ts", tsConfigPath: null },
  plugins: [{ name: "@hey-api/client-fetch", baseUrl: false }],
};
