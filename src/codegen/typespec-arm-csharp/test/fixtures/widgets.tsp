// Snapshot fixture: exercises every shape the emitter supports (see ../emitter.test.js).
import "@typespec/http";
import "@typespec/streams";
import "@typespec/events";
import "@typespec/sse";

using Http;
using SSE;

@service(#{ title: "Fixture" })
@route("/api")
namespace Fixture;

enum Color {
  Red: "red",
  /** The default. */
  Green: "green",
}

enum Priority {
  Low: 1,
  High: 2,
}

model Circle {
  kind: "circle";
  radius: float64;
}

model Square {
  kind: "square";
  side: float64;
}

/** A shape, discriminated on `kind`. */
@discriminated(#{ envelope: "none", discriminatorPropertyName: "kind" })
union Shape {
  circle: Circle,
  square: Square,
}

/** A widget. */
model Widget {
  @visibility(Lifecycle.Read) @format("uuid") id: string;

  /** Set once, at creation. */
  @visibility(Lifecycle.Create, Lifecycle.Read) serial: string;

  /** Only sent when updating. */
  @visibility(Lifecycle.Update) note?: string;

  @minLength(1) @maxLength(50) name: string;
  color?: Color;
  priority: Priority;
  description: string | null;
  parent?: string | null;
  @maxItems(10) tags?: string[];
  labels?: Record<string>;
  @minValue(0) @maxValue(100) weight?: int32;
  createdAt: utcDateTime;
  shape: Shape;
}

model ResultProblem {
  message: string;
  tags: string[] | null;
  severity: int32;
  source: string | null;
  exceptionSummary: string | null;
}

@error
model BadRequest {
  @statusCode status: 400;
  @body problems: ResultProblem[];
}

@error
model NotFound {
  @statusCode status: 404;
  @body problems: ResultProblem[];
}

@route("/widgets")
interface Widgets {
  /** List widgets. */
  @get list(
    @query color?: Color,
    @query limit?: int32,
    @header("x-request-id") requestId?: string,
  ): Widget[];

  @post create(@body body: Widget): Widget | BadRequest;

  @put update(@path id: string, @query dryRun?: boolean, @body body: Widget):
    | Widget
    | BadRequest
    | NotFound;

  @delete delete(@path id: string): NoContentResponse | NotFound;
}

@route("/shapes")
interface Shapes {
  @get get(@path id: string): Shape | NotFound;
}

@route("/tags")
interface Tags {
  /** Echoes a required `explode: false` list (`?names=a,b`). */
  @get echo(@query(#{ explode: false }) names: string[]): string[];
}

// --- server-sent events ----------------------------------------------------------------------

/** Keeps the connection alive. */
model Ping {
  type: "ping";
  at: utcDateTime;
}

// Carries the nested Shape union rather than a Widget: @typespec/openapi3 0.86 fails with
// duplicate-type-name when an event nests a model with properties invisible on read (Widget's
// update-only `note`).
model WidgetChanged {
  type: "widget.changed";
  at: utcDateTime;
  @format("uuid") widgetId: string;
  name: string;
  shape: Shape;
}

model WidgetRemoved {
  type: "widget.removed";
  at: utcDateTime;
  @format("uuid") widgetId: string;
}

/** The widget event stream. */
@TypeSpec.Events.events
union WidgetEvent {
  @TypeSpec.Events.contentType("application/json") ping: Ping,
  @TypeSpec.Events.contentType("application/json") `widget.changed`: WidgetChanged,
  @TypeSpec.Events.contentType("application/json") `widget.removed`: WidgetRemoved,
}

@route("/widget-events")
interface WidgetEvents {
  /** Streams widget events; `type` limits them to these event names. */
  @get stream(
    @query(#{ explode: false }) type?: string[],
    @header("Last-Event-ID") lastEventId?: string,
  ): SSEStream<WidgetEvent> | BadRequest;
}
