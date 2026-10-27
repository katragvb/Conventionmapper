# 0002. Values the caller owns are method parameters

**Status:** Accepted

## Context

Typical mapping code builds an object in two steps: `var dto = order.ToDto(); dto.CreatedBy = user; dto.Note = note;`. A forgotten
assignment is a silent default, the object is half-built in between, and the data-flow report can't see the assignments.

## Decision

Values that come from the caller rather than the source object are parameters of the mapping method:
`ToDto(this Order order, int createdBy, string? note)`. A parameter sets the destination member of the same name (or starts a
`[MapMember]` path), and wins over a source member with that name because the caller supplies it on purpose. Overloads share
configuration with `[MapInherit]`. Parameters that only ever receive a constant become `[MapConstant]` instead.

## Consequences

A forgotten value is a compile error. The report shows parameters as the source of those members. CM0023 and its fix move
assignments after a map into parameters. A parameter that sets nothing is error CM0011, so a renamed member can't silently cut
off the caller's value.
