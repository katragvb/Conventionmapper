# 0004. Exact case decides `[MapMember]` sources

**Status:** Accepted

## Context

Member names are matched case-insensitively, so in `[MapMember("CreateUser", "Creator")]` the path could mean the source member
`CreateUser` or a parameter `createUser`. An earlier version reported every such case as ambiguous, which forced unnatural
parameter names in real code.

## Decision

The exact case decides: `"CreateUser"` is the source member, `"createUser"` the parameter. A path starting with the source
parameter's name (`"source.CreateUser"`) always means the source. Only a path that matches both, in neither's exact case, is
error CM0014.

## Consequences

Parameters keep natural camelCase names. The rule is easy to state and to check, and an accidental case mismatch still fails
the build instead of guessing.
