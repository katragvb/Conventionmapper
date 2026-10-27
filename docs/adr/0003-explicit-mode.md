# 0003. Explicit mode for copies that matter

**Status:** Accepted

## Context

By convention every destination member with a matching source member is copied. That is right for DTOs, and wrong for copies
between rows or into external contracts: a column added later to both types starts being copied without anyone noticing,
including keys and audit columns.

## Decision

`[MapExplicit(...)]` (on a method or the mapper class) inverts the default: only the members named there, in `[MapMember]`,
`[MapConstant]`, `[MapFrom]` or by a parameter are set; everything else keeps its default. Listed members still need a source
(CM0001), and listed names that don't exist are CM0008.

## Consequences

Entity-to-entity copies and migrated hand-written copies (the CM0029 fix) use explicit mode, so they keep setting exactly what
they set. The cost is a longer attribute list, which the IDE fixes maintain. Nested objects mapped through generated helpers
still follow conventions unless their own mapping method is explicit.
