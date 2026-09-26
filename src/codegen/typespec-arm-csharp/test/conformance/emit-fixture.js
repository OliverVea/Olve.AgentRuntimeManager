// Compiles the widgets fixture (../fixtures/widgets.tsp) the way the repo compiles its own spec
// (tspconfig.yaml): OpenAPI 3.2 JSON via @typespec/openapi3, and the C# surface via this emitter.
// Output goes to <repo>/artifacts/codegen/conformance/{spec,generated}, never into src/.
// Invoked by Conformance.csproj before it builds; fails on any error diagnostic.

import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { compile, NodeHost } from "@typespec/compiler";

const here = dirname(fileURLToPath(import.meta.url));
const emitterDir = join(here, "..", "..");
const repoRoot = join(emitterDir, "..", "..", "..");
const outDir = join(repoRoot, "artifacts", "codegen", "conformance");

const program = await compile(NodeHost, join(here, "..", "fixtures", "widgets.tsp"), {
  emit: ["@typespec/openapi3", emitterDir],
  options: {
    "@typespec/openapi3": {
      "openapi-versions": ["3.2.0"],
      "file-type": "json",
      "emitter-output-dir": join(outDir, "spec"),
    },
    "typespec-arm-csharp": {
      "emitter-output-dir": join(outDir, "generated"),
      namespace: "Fixture.Api",
      "external-types": { ResultProblem: "ResultProblem" },
    },
  },
});

for (const d of program.diagnostics) console.error(`${d.severity} ${d.code}: ${d.message}`);
if (program.diagnostics.some((d) => d.severity === "error")) process.exit(1);
console.log(`widgets fixture emitted to ${outDir}`);
