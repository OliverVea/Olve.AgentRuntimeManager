import "@typespec/http";

using Http;

namespace Arm;

/** The body of every error response. */
model ErrorEnvelope {
  error: ErrorBody;
}

model ErrorBody {
  /** Stable, `SCREAMING_SNAKE_CASE` (`SESSION_NOT_FOUND`); `INVALID_REQUEST` for malformed or invalid input. */
  code: string;

  message: string;
  details: ErrorDetails;
}

model ErrorDetails {
  /** Every problem found, when there was more than one (e.g. several invalid fields). */
  problems?: ErrorProblem[];
}

model ErrorProblem {
  code: string;
  message: string;
}

// No doc comment: it would replace each status's standard description in the OpenAPI.
@error
model ErrorResponse<Status extends int32> {
  @statusCode status: Status;
  @body error: ErrorEnvelope;
}

@error model BadRequest is ErrorResponse<400>;
@error model NotFound is ErrorResponse<404>;
@error model Conflict is ErrorResponse<409>;
@error model ServiceUnavailable is ErrorResponse<503>;
