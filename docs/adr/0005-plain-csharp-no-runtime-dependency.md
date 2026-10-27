# 0005. The generator writes plain C# with no runtime dependency

**Status:** Accepted

## Context

Mapping is on hot paths, runs in trimmed and Native AOT apps, and has to be debugged when it goes wrong. Reflection-based
mappers pay a startup cost, need configuration lookups per call, and are opaque in a debugger.

## Decision

The source generator emits ordinary C#: assignments, loops, constructor calls and `switch` statements, with no reflection, no
delegates per member and no runtime library. The mapping attributes are generated into the project and marked
`[Conditional("CONVENTIONMAPPER_KEEP_ATTRIBUTES")]`, so they don't exist in the compiled assembly. Generated code is
culture-invariant and works on every target framework, including .NET Framework and netstandard2.0.

## Consequences

The generated code is as fast as hand-written code and can be stepped through. Features that need runtime configuration (DI
resolvers, open generics, `Patch` by configuration) stay in the runtime package, which is honest about not supporting Native
AOT: its entry points carry `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. CI publishes and runs a Native AOT sample
with warnings as errors on every build.
