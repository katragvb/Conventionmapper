# 0007. `[Sensitive]` data flows only into `[Sensitive]` members

**Status:** Accepted

## Context

Password hashes, tokens and personal data leak through ordinary code: a DTO that gained a field, a log line that prints an
object, an exception message. Code review rarely catches these, because the leaking line looks harmless.

## Decision

A member marked `[Sensitive]` may only be copied into another `[Sensitive]` member. A map that would do otherwise is an error
(CM0009 in the generator, CM1003 and a startup failure in the runtime package). The same metadata drives the rest: CM0032 flags
`[Sensitive]` data passed to loggers, exception messages and `ToString()`; `[MapRedact]` and `[MapLogString]` produce safe
copies and text; diffs show `***`; CM0026 suggests the attribute for personal-looking names.

## Consequences

Protection is opt-in per member but enforced everywhere once declared. Marking a destination `[Sensitive]` is the explicit way to
allow a flow, which keeps the decision in the code and in the report's "Sensitive members" section.
