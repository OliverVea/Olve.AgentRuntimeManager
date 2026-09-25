import "@typespec/http";

using Http;

/** ARM API contract. Source of truth — see docs/SPEC-FIRST.md (M1). */
@service(#{ title: "Olve.AgentRuntimeManager" })
@route("/api")
namespace Arm;

model Message {
  @visibility(Lifecycle.Read) id: string;
  @maxLength(280) text: string;
}

/** SPEC §Error Model envelope. */
model ErrorBody {
  code: string;
  message: string;
  details?: Record<unknown>;
}

@error
model BadRequest {
  @statusCode status: 400;
  error: ErrorBody;
}

@error
model NotFound {
  @statusCode status: 404;
  error: ErrorBody;
}

@route("/messages")
interface Messages {
  @post create(@body body: Message):
    | {
        @statusCode status: 201;
        @body message: Message;
      }
    | BadRequest;

  @get get(@path id: string): Message | NotFound;
}
