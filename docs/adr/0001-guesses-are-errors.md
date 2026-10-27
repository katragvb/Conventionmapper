# 0001. Guesses are build errors

**Status:** Accepted

## Context

Convention-based mappers fail quietly: when a destination member could come from two places (`Total` and `GetTotal()`, `Id` and
`ID`, two flattenings, a parameter and a flattened path), most libraries pick one by an internal rule. The wrong pick compiles,
passes a casual test, and corrupts data in production. When a member has no source at all, the same libraries leave it at its
default.

## Decision

The generator never picks between possible sources, and never leaves a destination member unset without being told.

- No source for a member: error CM0001.
- More than one possible source: error CM0015.
- An attribute that names a member that doesn't exist: warning CM0008.

The fix is always an explicit statement in code: `[MapMember]`, `[MapIgnore]`, a parameter, or `[MapExplicit]`.

## Consequences

New maps take a minute longer to get compiling, and every decision is visible in the declaration and the data-flow report. IDE
fixes write the attributes, so the cost stays small. A convention that would introduce a new kind of guess has to come with its
own error for the ambiguous case.
