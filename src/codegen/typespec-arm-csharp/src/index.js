// typespec-arm-csharp: emits the C# minimal-API surface of the ARM contract (docs/SPEC-FIRST.md, M2).
//
//   ArmModels.g.cs       records (request shapes split by visibility), enums, discriminated
//                        unions, SSE event unions (IArmEvent, EventTypes), and IValidator<T>s
//                        for request bodies from @maxLength etc.
//   ArmApi.g.cs          per operation: a request record, a response union with one variant per
//                        declared status (an SSE variant carries IAsyncEnumerable<ArmSseItem<TEvent>>)
//                        and I…Handler : IArmHandler<Req, Response>; ArmOperations (declared
//                        statuses), ArmApi.HandlerTypes, MapArmApi(), and ArmParameters
//                        (string-enum parameters parsed by wire value)
//   ArmJsonContext.g.cs  System.Text.Json source-gen context for every DTO
//
// The generated code relies on hand-written types in the same namespace, kept in the backend
// source (Api/): IArmHandler, IArmResponse, ArmOperation (the operation table's row), the error
// envelope (ArmErrorEnvelope, ArmErrors), ArmValidation, ArmBindingFailures, and the SSE pieces
// (IArmEvent, ArmSseItem, ArmQuery, ArmServerSentEventsResult).
// test/conformance compiles the output with that runtime and proves it matches the spec over HTTP.

import { emitFile, resolvePath } from "@typespec/compiler";
import { collect } from "./collect.js";
import { renderApi, renderJsonContext, renderModels } from "./render.js";

export { $lib } from "./lib.js";

export async function $onEmit(context) {
  const { program, options } = context;
  const namespace = options.namespace ?? "Arm.Generated";
  const ir = collect(program, options);
  if (program.compilerOptions.noEmit) return;

  const files = {
    "ArmModels.g.cs": renderModels(ir, namespace),
    "ArmApi.g.cs": renderApi(ir, namespace),
    "ArmJsonContext.g.cs": renderJsonContext(ir, namespace),
  };
  for (const [name, content] of Object.entries(files)) {
    await emitFile(program, { path: resolvePath(context.emitterOutputDir, name), content, newLine: "lf" });
  }
}
