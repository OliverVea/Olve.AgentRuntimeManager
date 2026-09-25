import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

// In dev, the browser talks to the Vite dev server (same origin) and Vite proxies the
// API paths to the real backend — no CORS, no base-URL juggling in the component.
//
//   VITE_API_TARGET   backend the dev proxy forwards to (default: the port-forward from
//                     `kubectl port-forward svc/olve-arm 18080:80`, or a local
//                     `dotnet run`). On the tailnet: https://olve-arm-beta.ovea.pro
//
// In a production build the component reads VITE_API_BASE_URL (falling back to same-origin),
// so deploy the static bundle behind the same host that serves the API, or set it explicitly.
export default defineConfig(() => {
  const target = process.env.VITE_API_TARGET ?? "http://localhost:18080";
  return {
    // `@arm/client` is the Hey API client generated from src/spec/main.tsp into the repo-root
    // artifacts/ (never committed; `npm run generate` — run by the pre-hooks — rebuilds it).
    // Mirrored by `paths` in tsconfig.json; applies to vitest too.
    resolve: {
      alias: {
        "@arm/client": fileURLToPath(new URL("../../artifacts/clients/ts", import.meta.url)),
      },
    },
    server: {
      // The SPA calls the API under /api (same-origin); Vite forwards that to the backend.
      proxy: {
        "/api": { target, changeOrigin: true },
      },
    },
    // Unit tests are opt-in (`npm test`) and live next to the source they cover
    // (e.g. src/base-element.test.ts). happy-dom gives custom elements a shadow DOM.
    test: {
      environment: "happy-dom",
      include: ["src/**/*.{test,spec}.ts"],
    },
  };
});
