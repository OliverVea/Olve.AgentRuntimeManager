// A shape the emitter can't type: an untagged union must be reported, not silently mistyped.
import "@typespec/http";

using Http;

@service
namespace Unsupported;

model A {
  x: string | int32;
}

@get op get(): A;
