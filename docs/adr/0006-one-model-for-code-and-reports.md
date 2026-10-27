# 0006. Code, report, write sets and lineage come from one model

**Status:** Accepted

## Context

Reviewers see attribute changes, not what they do. Documentation of data flows written by hand drifts from the code the day
after it is written, and test expectations listed by hand drift the same way.

## Decision

While generating a method, the generator records every flow once: destination member, source path or value, and notes. The
generated code, the data-flow report (Markdown, CSV, JSON, Mermaid), `WriteSets`, `Lineage` and the optional XML docs are all
produced from that record. No artifact is derived by re-analysing another.

## Consequences

The report and the code cannot disagree, so the report can be snapshot-tested (`DataFlowReport.AssertMatchesDirectory`) and
`WriteSets` / `Lineage` can be checked against real behavior (`MapperAssert.WritesExactly`, `WritesFrom`,
`AllMappersMatchWriteSets`). Every new kind of value (a constant, a computed member, a condition) has to record a flow, or it
won't show up anywhere.
