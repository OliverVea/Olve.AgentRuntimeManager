import "@typespec/http";

using Http;

namespace Arm;

/** A repeated key within 24 hours returns the original response instead of acting again. */
model IdempotencyKey {
  @header("Idempotency-Key") idempotencyKey?: string;
}
