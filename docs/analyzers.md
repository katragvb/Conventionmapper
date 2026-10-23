# Analyzers for the runtime API

The `ConventionMapper` package includes a Roslyn analyzer and code fixes. They run in the IDE while you type and during `dotnet build`, so configuration mistakes show up before the app runs.

| Id | Default | What it reports | Code fix |
| --- | --- | --- | --- |
| CM1001 | Warning | `CreateMap<TSource, TDestination>();` where a destination member has no matching source member (same name, `Get` method or flattening). `AssertConfigurationIsValid()` would fail at runtime. | Adds `.ForMember(d => d.X, o => o.Ignore())`, one member or all at once |
| CM1002 | Info | `mapper.Map<TDestination>(source)` where the source type is known at compile time (sealed class or struct). | Rewrites to `mapper.Map<TSource, TDestination>(source)`, which uses the static fast path |

CM1001 only checks bare `CreateMap<,>();` statements. Maps configured further in the same statement (`ForMember`, `ConvertUsing`, `ReverseMap`, ...) are skipped, and so are profiles that change naming conventions (`RecognizePrefixes`, `ReplaceMemberName`, `AddGlobalIgnore`, ...), because the analyzer cannot see the effect of those settings.

To make unmapped members fail the build:

```ini
# .editorconfig
[*.cs]
dotnet_diagnostic.CM1001.severity = error
```

The build-time generator (`ConventionMapper.SourceGenerator`) has its own diagnostics, CM0001 to CM0008; see [source-generator.md](source-generator.md).
