// Walks the TypeSpec HTTP program and builds a small, C#-shaped model of it (the "IR") that
// render.js turns into source text. All TypeSpec knowledge lives here; render.js only formats.

import {
  getDiscriminatedUnion,
  getDoc,
  getFormat,
  getMaxItems,
  getMaxLength,
  getMaxValue,
  getMinItems,
  getMinLength,
  getMinValue,
  isArrayModelType,
  isNullType,
  isRecordModelType,
  isTemplateInstance,
} from "@typespec/compiler";
import {
  getAllHttpServices,
  getVisibilitySuffix,
  isMetadata,
  isVisible,
  resolveRequestVisibility,
  Visibility,
} from "@typespec/http";
import { getContentType, isEvents } from "@typespec/events";
import { getStreamOf } from "@typespec/streams";
import { reportDiagnostic } from "./lib.js";
import { camel, ordinal, pascal } from "./names.js";

// TypeSpec std scalars → C# types. Custom scalars resolve through their base scalar.
const SCALARS = {
  string: "string",
  boolean: "bool",
  int8: "sbyte",
  int16: "short",
  int32: "int",
  int64: "long",
  safeint: "long",
  integer: "long",
  uint8: "byte",
  uint16: "ushort",
  uint32: "uint",
  uint64: "ulong",
  float32: "float",
  float64: "double",
  float: "double",
  numeric: "double",
  decimal: "decimal",
  decimal128: "decimal",
  utcDateTime: "DateTimeOffset",
  offsetDateTime: "DateTimeOffset",
  plainDate: "DateOnly",
  plainTime: "TimeOnly",
  url: "string",
  bytes: "byte[]",
};
const VALUE_TYPES = new Set([
  "bool", "sbyte", "short", "int", "long", "byte", "ushort", "uint", "ulong", "float", "double", "decimal",
  "DateTimeOffset", "DateOnly", "TimeOnly", "Guid", "System.Text.Json.JsonElement",
]);
const JSON_ELEMENT = "System.Text.Json.JsonElement";
const EVENT_STREAM = "text/event-stream";

const READ = Visibility.Read;
const WRITE = Visibility.Create | Visibility.Update;

/**
 * @returns {{
 *   models: Map<string, object>, unions: Map<string, object>, enums: Map<string, object>,
 *   operations: object[], validators: object[], jsonTypes: string[]
 * }}
 */
export function collect(program, options = {}) {
  const external = options["external-types"] ?? {};
  const models = new Map(); // C# name → { name, doc, writableOf?, union?, props }
  const unions = new Map(); // C# name → { name, doc, discriminator, variants: [{ key, name }] }
  const enums = new Map(); // C# name → { name, doc, numeric, members }
  const warned = new Set();

  function warnType(target, shape) {
    if (warned.has(target)) return;
    warned.add(target);
    reportDiagnostic(program, { code: "unsupported-type", format: { shape }, target });
  }

  // ---- naming ------------------------------------------------------------------------------

  function baseName(model, hint) {
    if (!model.name) return hint;
    if (!isTemplateInstance(model)) return model.name;
    // `Page<Message>` → PageMessage
    const args = model.templateMapper?.args ?? [];
    return model.name + args.map((a) => pascal(a.name ?? "")).join("");
  }

  function allProperties(model) {
    const byName = new Map();
    const chain = [];
    for (let m = model; m; m = m.baseModel) chain.unshift(m);
    for (const m of chain) for (const p of m.properties.values()) byName.set(p.name, p);
    return [...byName.values()].filter((p) => !isMetadata(program, p));
  }

  const propsFor = (model, vis) => allProperties(model).filter((p) => isVisible(program, p, vis));
  const propKey = (model, vis) => propsFor(model, vis).map((p) => p.name).join(",");

  /**
   * The C# name of `model` as seen with visibility `vis`:
   * - the same properties as the read shape → the model's own name (`Message`);
   * - the same as everything writable (Create|Update) → `MessageWritable`;
   * - otherwise named per visibility (`MessageCreate`, `MessageUpdate`, …).
   */
  function visibleName(model, vis, hint) {
    const base = baseName(model, hint);
    const key = propKey(model, vis);
    if (key === propKey(model, READ)) return base;
    if (key === propKey(model, WRITE)) return `${base}Writable`;
    return base + getVisibilitySuffix(vis & (Visibility.All | Visibility.Item), READ);
  }

  // `op(): Widget` can reach us as an anonymous copy of Widget (`{ ...Widget }`) when some of
  // its properties aren't visible in the response. Name it after the model it copies.
  function unwrapSpread(model) {
    while (!model.name && model.sourceModels?.length === 1 && model.sourceModels[0].usage === "spread") {
      const source = model.sourceModels[0].model;
      // A visibility-filtered copy has a subset of the source's properties; anything extra
      // (`{ ...Widget, more: string }`) makes it a model of its own.
      const sourceNames = new Set();
      for (let m = source; m; m = m.baseModel) for (const name of m.properties.keys()) sourceNames.add(name);
      if (![...model.properties.keys()].every((name) => sourceNames.has(name))) break;
      model = source;
    }
    return model;
  }

  function modelType(model, vis, hint) {
    model = unwrapSpread(model);
    if (external[model.name]) return external[model.name];
    const name = visibleName(model, vis, hint);
    if (models.has(name)) return name;
    const base = baseName(model, hint);
    const entry = { name, doc: getDoc(program, model), writableOf: name !== base ? base : undefined, props: [] };
    models.set(name, entry); // register before recursing: models may be self-referential
    entry.props = propsFor(model, vis).map((p) => property(p, vis, name));
    return name;
  }

  function property(p, vis, owner) {
    const type = csType(p.type, vis, p, owner + pascal(p.name));
    const nullable = type.endsWith("?");
    let name = pascal(p.name);
    if (name === owner) name += "Value"; // CS0542: a member can't share its enclosing type's name
    return {
      wire: p.name,
      name,
      type: p.optional && !nullable ? `${type}?` : type,
      baseType: type.replace(/\?$/, ""),
      optional: !!p.optional,
      nullable,
      doc: getDoc(program, p),
      constraints: {
        minLength: getMinLength(program, p),
        maxLength: getMaxLength(program, p),
        minValue: getMinValue(program, p),
        maxValue: getMaxValue(program, p),
        minItems: getMinItems(program, p),
        maxItems: getMaxItems(program, p),
      },
    };
  }

  // ---- types -------------------------------------------------------------------------------

  function scalarType(type, prop) {
    const format = (prop && getFormat(program, prop)) ?? getFormat(program, type);
    for (let s = type; s; s = s.baseScalar) {
      if (s.name === "string" && (format === "uuid" || getFormat(program, s) === "uuid")) return "Guid";
      if (s.namespace?.name === "TypeSpec" && SCALARS[s.name]) return SCALARS[s.name];
    }
    warnType(type, `scalar '${type.name}'`);
    return JSON_ELEMENT;
  }

  function enumType(type) {
    const name = type.name;
    if (!enums.has(name)) {
      const members = [...type.members.values()].map((m) => ({
        name: pascal(m.name),
        value: m.value ?? m.name,
        doc: getDoc(program, m),
      }));
      enums.set(name, { name, doc: getDoc(program, type), numeric: members.some((m) => typeof m.value === "number"), members });
    }
    return name;
  }

  function unionType(type, vis, prop, hint) {
    const variants = [...type.variants.values()].map((v) => v.type);
    const nonNull = variants.filter((t) => !isNullType(t));
    const q = nonNull.length < variants.length ? "?" : "";
    if (nonNull.length === 1) return csType(nonNull[0], vis, prop, hint) + q;

    // `"a" | "b"` (and named unions of string literals/string) → string.
    if (nonNull.every((t) => t.kind === "String" || (t.kind === "Scalar" && scalarType(t) === "string"))) return `string${q}`;

    const [disc] = getDiscriminatedUnion(program, type);
    if (disc && type.name && disc.options.envelope === "none") {
      const discriminator = disc.options.discriminatorPropertyName;
      const named = [...disc.variants].map(([key, t]) => ({ key, name: csType(t, vis, undefined, type.name + pascal(key)) }));
      // Keep the union's name in step with its variants' visibility naming (`Pet` / `PetWritable`).
      const suffix = [...disc.variants].map(([, t], i) => named[i].name.slice(baseName(t, "").length)).find(Boolean) ?? "";
      registerUnion({ name: type.name + suffix, doc: getDoc(program, type), discriminator, variants: named });
      return type.name + suffix + q;
    }
    warnType(type, disc ? "discriminated union with an envelope" : "union without a discriminator");
    return JSON_ELEMENT + q;
  }

  /** Registers a polymorphic union; its variant records derive from it and lose the discriminator property. */
  function registerUnion(union) {
    if (unions.has(union.name)) return;
    unions.set(union.name, union);
    for (const v of union.variants) {
      const model = models.get(v.name);
      if (model) {
        model.union = { name: union.name, discriminator: union.discriminator, key: v.key, events: !!union.events };
        model.props = model.props.filter((p) => p.wire !== union.discriminator);
      }
    }
  }

  /**
   * The `@events` union an SSE response streams, as a polymorphic C# union discriminated on
   * `type`. Every variant must be named (the SSE event name), carry JSON, and be a model whose
   * `type` is that same name, so the event name and the data's `type` can't disagree.
   * @returns the union's C# name, or undefined (with a diagnostic) when it doesn't fit.
   */
  function eventsType(union, unsupported) {
    if (union?.kind !== "Union" || !isEvents(program, union) || !union.name) {
      unsupported("an SSE stream must be of a named @events union");
      return undefined;
    }
    const variants = [];
    for (const [key, variant] of union.variants) {
      const model = variant.type;
      const typeProp = model.kind === "Model" ? model.properties.get("type") : undefined;
      if (typeof key !== "string") {
        unsupported(`event variants of '${union.name}' must be named (the SSE event name)`);
        return undefined;
      }
      if (getContentType(program, variant) !== "application/json") {
        unsupported(`event '${key}' of '${union.name}' must be @contentType("application/json")`);
        return undefined;
      }
      if (typeProp?.type.kind !== "String" || typeProp.type.value !== key) {
        unsupported(`event '${key}' of '${union.name}' must be a model with \`type: "${key}"\``);
        return undefined;
      }
      variants.push({ key, name: csType(model, READ, undefined, union.name + pascal(key)) });
    }
    registerUnion({ name: union.name, doc: getDoc(program, union), discriminator: "type", variants, events: true });
    return union.name;
  }

  function csType(type, vis, prop, hint) {
    switch (type.kind) {
      case "Scalar":
        return scalarType(type, prop);
      case "Enum":
        return enumType(type);
      case "Boolean":
        return "bool";
      case "String":
        return "string";
      case "Number":
        return Number.isInteger(type.value) ? "int" : "double";
      case "Model":
        if (isArrayModelType(type)) return `IReadOnlyList<${csType(type.indexer.value, vis, undefined, `${hint}Item`)}>`;
        if (isRecordModelType(type)) {
          return `IReadOnlyDictionary<string, ${csType(type.indexer.value, vis, undefined, `${hint}Value`)}>`;
        }
        return modelType(type, vis, hint);
      case "Union":
        return unionType(type, vis, prop, hint);
      case "UnionVariant":
        return csType(type.type, vis, prop, hint);
      case "Intrinsic":
        if (type.name !== "unknown") warnType(type, `intrinsic '${type.name}'`);
        return JSON_ELEMENT;
      default:
        warnType(type, type.kind);
        return JSON_ELEMENT;
    }
  }

  // ---- operations --------------------------------------------------------------------------

  const [services] = getAllHttpServices(program);
  const operations = [];
  for (const http of services.flatMap((s) => s.operations)) {
    const op = http.operation;
    const group = op.interface?.name ?? "";
    const name = group + pascal(op.name);
    const operationId = group ? `${group}_${op.name}` : op.name;
    const unsupported = (reason) =>
      reportDiagnostic(program, { code: "unsupported-operation", format: { operation: operationId, reason }, target: op });
    const reqVis = resolveRequestVisibility(program, op, http.verb);

    const params = [];
    for (const p of http.parameters.parameters) {
      if (!["path", "query", "header"].includes(p.type)) {
        unsupported(`${p.type} parameter '${p.name}' is not supported`);
        continue;
      }
      let type = csType(p.param.type, reqVis, p.param, name + pascal(p.param.name));
      // Arrays: only `@query(#{ explode: false })` lists of strings (`?event=a,b`) so far.
      const list = isArrayModelType(p.param.type);
      if (list && (p.type !== "query" || p.explode || type !== "IReadOnlyList<string>")) {
        unsupported(`array parameter '${p.name}' must be a string list in the query with explode: false`);
        continue;
      }
      if (p.param.optional && !type.endsWith("?")) type += "?";
      params.push({ kind: p.type, wire: p.name, name: pascal(p.param.name), local: camel(p.param.name), type, list });
    }

    let body;
    const b = http.parameters.body;
    if (b?.bodyKind === "multipart") unsupported("multipart bodies are not supported");
    else if (b?.type) body = { type: csType(b.type, reqVis, undefined, `${name}Body`) };

    let success;
    const errors = [];
    for (const r of http.responses) {
      if (typeof r.statusCodes !== "number") {
        unsupported("status code ranges and default responses are not supported");
        continue;
      }
      const stream = r.responses.some((x) => x.body?.contentTypes?.includes(EVENT_STREAM));
      const bodyType = r.responses.find((x) => x.body)?.body?.type;
      const response = stream
        ? { status: r.statusCodes, type: eventsType(getStreamOf(program, r.type), unsupported), stream: true }
        : { status: r.statusCodes, type: bodyType ? csType(bodyType, READ, undefined, `${name}Response`) : undefined };
      if (stream && !response.type) continue;
      if (r.statusCodes >= 300) errors.push(response);
      else if (!success) success = response;
      else unsupported("multiple success statuses are not supported yet; only the first is generated");
    }
    success ??= { status: 204 };

    operations.push({ name, operationId, verb: http.verb, path: http.path, doc: getDoc(program, op), params, body, success, errors });
  }
  operations.sort((a, b) => ordinal(a.name, b.name));

  // ---- validators (request bodies with constraints) ----------------------------------------

  const validators = [];
  const bodyTypes = [...new Set(operations.map((o) => o.body?.type).filter(Boolean))].sort(ordinal);
  for (const typeName of bodyTypes) {
    const model = models.get(typeName);
    if (!model) continue;
    const rules = model.props.flatMap((p) => rulesFor(p, (t) => VALUE_TYPES.has(t) || enums.has(t)));
    if (rules.length) validators.push({ name: `${typeName}Validator`, model: typeName, rules });
  }
  for (const o of operations) if (o.body && validators.some((v) => v.model === o.body.type)) o.body.validator = `${o.body.type}Validator`;

  // ---- JSON source-gen roots ----------------------------------------------------------------

  const variantNames = new Set([...unions.values()].flatMap((u) => u.variants.map((v) => v.name)));
  const jsonTypes = new Set([
    ...[...models.keys()].filter((n) => !variantNames.has(n)),
    ...unions.keys(),
    ...enums.keys(),
    ...operations.flatMap((o) => [o.body?.type, o.success.type, ...o.errors.map((e) => e.type)]).filter(Boolean),
  ]);

  return {
    models: sortedMap(models),
    unions: sortedMap(unions),
    enums: sortedMap(enums),
    operations,
    validators,
    jsonTypes: [...jsonTypes].map((t) => t.replace(/\?$/, "")).filter((t) => !VALUE_TYPES.has(t)).sort(ordinal),
  };
}

/** Validation rules for one property, derived from the spec's constraints. */
function rulesFor(p, isValueType) {
  const rules = [];
  const c = p.constraints;
  const ref = !isValueType(p.baseType);
  const x = `value.${p.name}`;
  if (!p.optional && !p.nullable && ref) rules.push({ test: `${x} is null`, message: `'${p.wire}' is required.` });
  if (p.baseType === "string") {
    if (c.minLength !== undefined) rules.push({ test: `${x} is { Length: < ${c.minLength} }`, message: `'${p.wire}' must be at least ${plural(c.minLength, "character")}.` });
    if (c.maxLength !== undefined) rules.push({ test: `${x} is { Length: > ${c.maxLength} }`, message: `'${p.wire}' cannot exceed ${plural(c.maxLength, "character")}.` });
  }
  if (p.baseType.startsWith("IReadOnlyList<")) {
    if (c.minItems !== undefined) rules.push({ test: `${x} is { Count: < ${c.minItems} }`, message: `'${p.wire}' must have at least ${plural(c.minItems, "item")}.` });
    if (c.maxItems !== undefined) rules.push({ test: `${x} is { Count: > ${c.maxItems} }`, message: `'${p.wire}' cannot have more than ${plural(c.maxItems, "item")}.` });
  }
  const numeric = typeof c.minValue === "number" || typeof c.maxValue === "number";
  if (numeric && isValueType(p.baseType)) {
    if (c.minValue !== undefined) rules.push({ test: `${x} < ${c.minValue}`, message: `'${p.wire}' must be at least ${c.minValue}.` });
    if (c.maxValue !== undefined) rules.push({ test: `${x} > ${c.maxValue}`, message: `'${p.wire}' cannot exceed ${c.maxValue}.` });
  }
  return rules;
}

const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

function sortedMap(map) {
  return new Map([...map].sort(([a], [b]) => ordinal(a, b)));
}
