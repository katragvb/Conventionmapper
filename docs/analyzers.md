# Analyzers for the runtime API

The `ConventionMapper` package includes a Roslyn analyzer and code fixes. They run in the IDE while you type and during `dotnet build`, so configuration mistakes show up before the app runs.

| Id | Default | What it reports | Code fix |
| --- | --- | --- | --- |
| CM1001 | Warning | `CreateMap<TSource, TDestination>();` where a destination member has no matching source member (same name, `Get` method or flattening). `AssertConfigurationIsValid()` would fail at runtime. | Adds `.ForMember(d => d.X, o => o.Ignore())`, one member or all at once |
| CM1003 | Error | `CreateMap<TSource, TDestination>();` would copy a `[Sensitive]` source member into a destination member that is not marked `[Sensitive]`. The configuration would also fail at startup. | Adds `.ForMember(d => d.X, o => o.Ignore())` |
| CM1002 | Info | `mapper.Map<TDestination>(source)` where the source type is known at compile time (sealed class or struct). | Rewrites to `mapper.Map<TSource, TDestination>(source)`, which uses the static fast path |

CM1001 only checks bare `CreateMap<,>();` statements. Maps configured further in the same statement (`ForMember`, `ConvertUsing`, `ReverseMap`, ...) are skipped, and so are profiles that change naming conventions (`RecognizePrefixes`, `ReplaceMemberName`, `AddGlobalIgnore`, ...), because the analyzer cannot see the effect of those settings.

## Refactoring: Generate build-time mapper

On any `CreateMap<TSource, TDestination>()` call, the light bulb menu offers **Generate build-time mapper for TSource → TDestination**. It adds a file with the equivalent `[GeneratedMapper]` class for `ConventionMapper.SourceGenerator`, translating `Ignore()`, `MapFrom` member paths, `NullSubstitute` constants and `ReverseMap()`, and listing other options to convert by hand. See [source-generator.md](source-generator.md#moving-a-runtime-map-to-the-generator).

## Severity

To make unmapped members fail the build:

```ini
# .editorconfig
[*.cs]
dotnet_diagnostic.CM1001.severity = error
```

The package also reports CM0032 when an object with `[Sensitive]` members is passed to a logger, an exception message or a
`ToString()` (when `ConventionMapper.SourceGenerator` is also referenced, its copy of the check reports instead).

Every code, including the build-time generator's (`ConventionMapper.SourceGenerator`, CM0001 to CM0033), is described in
[diagnostics.md](diagnostics.md).
