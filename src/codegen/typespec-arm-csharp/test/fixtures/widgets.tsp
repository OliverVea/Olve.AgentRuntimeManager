// Snapshot fixture: exercises every shape the emitter supports (see ../emitter.test.js).
import "@typespec/http";

using Http;

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
