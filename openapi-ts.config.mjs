// Hey API client generation: artifacts/spec/openapi.json → artifacts/clients/ts (gitignored).
// baseUrl: false — no baked-in default; callers configure the client (same-origin in the SPA,
// --url / ARM_URL in the CLI).
export default {
  input: "artifacts/spec/openapi.json",
  // tsConfigPath: null — the repo root has no tsconfig, and without this Hey API reads every parent
  // folder looking for one; it crashes on one it may not list (an agent's workplace on the VMs).
  output: { path: "artifacts/clients/ts", tsConfigPath: null },
  plugins: [{ name: "@hey-api/client-fetch", baseUrl: false }],
};
