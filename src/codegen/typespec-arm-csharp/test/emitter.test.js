// Snapshot tests: compile test/fixtures/<name>.tsp with the emitter and compare every emitted
// file with test/snapshots/<name>/. A missing or different snapshot fails; to accept new output,
// run `npm run test:update` (UPDATE_SNAPSHOTS=1) and review the diff.

import assert from "node:assert/strict";
import { mkdtemp, mkdir, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { compile, NodeHost } from "@typespec/compiler";

const here = dirname(fileURLToPath(import.meta.url));
const emitterDir = join(here, "..");
const update = process.env.UPDATE_SNAPSHOTS === "1";

async function emit(fixture, options = {}) {
  const outputDir = await mkdtemp(join(tmpdir(), "arm-csharp-"));
  try {
    const program = await compile(NodeHost, join(here, "fixtures", `${fixture}.tsp`), {
      emit: [emitterDir],
      options: { "typespec-arm-csharp": { "emitter-output-dir": outputDir, ...options } },
    });
    const files = {};
    for (const name of (await readdir(outputDir)).sort()) files[name] = await readFile(join(outputDir, name), "utf8");
    return { diagnostics: program.diagnostics, files };
  } finally {
    await rm(outputDir, { recursive: true, force: true });
  }
}

async function matchSnapshots(fixture, files) {
  const dir = join(here, "snapshots", fixture);
  if (update) {
    await rm(dir, { recursive: true, force: true });
    await mkdir(dir, { recursive: true });
    for (const [name, content] of Object.entries(files)) await writeFile(join(dir, name), content);
    return;
  }
  const expected = (await readdir(dir).catch(() => [])).sort();
  assert.deepEqual(Object.keys(files), expected, `emitted files differ from test/snapshots/${fixture}/ (UPDATE_SNAPSHOTS=1 to accept)`);
  for (const name of expected) {
    assert.equal(files[name], await readFile(join(dir, name), "utf8"), `${fixture}/${name} differs from its snapshot`);
  }
}

const diagnosticCodes = (diagnostics) => diagnostics.map((d) => `${d.severity} ${d.code}: ${d.message}`);

test("widgets fixture matches its snapshots", async () => {
  const { diagnostics, files } = await emit("widgets", { namespace: "Fixture.Api", "external-types": { ErrorEnvelope: "ArmErrorEnvelope" } });
  assert.deepEqual(diagnosticCodes(diagnostics), []);
  await matchSnapshots("widgets", files);
});

test("output is deterministic", async () => {
  const options = { "external-types": { ErrorEnvelope: "ArmErrorEnvelope" } };
  const [a, b] = [await emit("widgets", options), await emit("widgets", options)];
  assert.deepEqual(a.files, b.files);
});

const record = (source, name) => source.match(new RegExp(`public sealed record ${name}\\n\\{[\\s\\S]*?\\n\\}`))?.[0] ?? "";

test("read-only properties are split into per-visibility request shapes", async () => {
  const models = (await emit("widgets")).files["ArmModels.g.cs"];
  // Response: id, no update-only note. POST (Create): serial, no id/note. PUT (Create|Update): serial and note.
  assert.match(record(models, "Widget"), /Guid Id/);
  assert.doesNotMatch(record(models, "Widget"), / Note /);
  assert.match(record(models, "WidgetCreate"), / Serial /);
  assert.doesNotMatch(record(models, "WidgetCreate"), / Note | Id /);
  assert.match(record(models, "WidgetWritable"), / Serial /);
  assert.match(record(models, "WidgetWritable"), / Note /);
  assert.doesNotMatch(record(models, "WidgetWritable"), / Id /);
});

test("unsupported shapes are reported, not silently mistyped", async () => {
  const { diagnostics } = await emit("unsupported");
  assert.deepEqual(diagnosticCodes(diagnostics), [
    "warning typespec-arm-csharp/unsupported-type: Unsupported type shape (union without a discriminator); emitted as System.Text.Json.JsonElement.",
    "warning typespec-arm-csharp/unsupported-operation: Operation 'tags': array parameter 'tag' must be a string list in the query with explode: false.",
    "warning typespec-arm-csharp/unsupported-operation: Operation 'ticks': event 'tick' of 'Ticks' must be a model with `type: \"tick\"`.",
  ]);
});

test("SSE operations stream their @events union with ids; explode:false lists bind from one string", async () => {
  const { files } = await emit("widgets", { "external-types": { ErrorEnvelope: "ArmErrorEnvelope" } });
  const api = files["ArmApi.g.cs"];
  const models = files["ArmModels.g.cs"];
  assert.match(api, /IWidgetEventsStreamHandler : IArmHandler<WidgetEventsStreamRequest, WidgetEventsStreamResponse>/);
  assert.match(api, /record Ok\(IAsyncEnumerable<ArmSseItem<WidgetEvent>> Events\) : WidgetEventsStreamResponse/);
  assert.match(api, /ToHttpResult\(\) => new ArmServerSentEventsResult<WidgetEvent>\(Events\)/);
  assert.match(api, /await handler\.HandleAsync\(new WidgetEventsStreamRequest\(ArmQuery\.List\(type\), lastEventId\), ct\)\)\.ToHttpResult\(\)/);
  assert.match(api, /\.Produces<WidgetEvent>\(200, "text\/event-stream"\)/);
  assert.match(models, /\[JsonPolymorphic\(TypeDiscriminatorPropertyName = "type"\)\]\n\[JsonDerivedType\(typeof\(Ping\), "ping"\)\]/);
  assert.match(models, /public abstract record WidgetEvent : IArmEvent/);
  assert.match(record(models, "WidgetChanged : WidgetEvent"), /override string EventType => "widget\.changed"/);
  // The discriminator is written by the polymorphism, not a property of its own.
  assert.doesNotMatch(record(models, "WidgetChanged : WidgetEvent"), /"type"/);
});

test("every declared response is a variant of the operation's response union", async () => {
  const { files } = await emit("widgets", { "external-types": { ErrorEnvelope: "ArmErrorEnvelope" } });
  const api = files["ArmApi.g.cs"];
  // 201 | 202 | 400: one variant each, named by status, each writing its own status.
  assert.match(api, /public abstract record WidgetsCreateResponse : IArmResponse/);
  assert.match(api, /public sealed record Created\(Widget Body\) : WidgetsCreateResponse[\s\S]*?Status => 201;/);
  assert.match(api, /public sealed record Accepted\(Widget Body\) : WidgetsCreateResponse[\s\S]*?Status => 202;/);
  assert.match(api, /public sealed record BadRequest\(ArmErrorEnvelope Body\) : WidgetsCreateResponse/);
  assert.match(api, /new\("Widgets_create", \[201, 202\], \[400\]\)/);
  // A success body only one variant carries converts implicitly; Widget is carried twice here.
  assert.doesNotMatch(api, /implicit operator WidgetsCreateResponse/);
  assert.match(api, /implicit operator WidgetsUpdateResponse\(Widget body\) => new Ok\(body\)/);
  // Errors never convert implicitly; interfaces can't.
  assert.doesNotMatch(api, /implicit operator \w+\(ArmErrorEnvelope/);
  assert.doesNotMatch(api, /implicit operator \w+\(IReadOnlyList/);
  // A bodyless response.
  assert.match(api, /public sealed record NoContent\(\) : WidgetsDeleteResponse[\s\S]*?TypedResults\.StatusCode\(204\)/);
});
