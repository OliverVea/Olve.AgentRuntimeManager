// Hey API client generation: artifacts/spec/openapi.json → artifacts/clients/ts (gitignored).
// baseUrl: false — no baked-in default; callers configure the client (same-origin in the SPA,
// --url / ARM_URL in the CLI).
export default {
  input: "artifacts/spec/openapi.json",
  output: "artifacts/clients/ts",
  plugins: [{ name: "@hey-api/client-fetch", baseUrl: false }],
};
