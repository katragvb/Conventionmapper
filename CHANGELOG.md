# Changelog

## 1.3.0 (unreleased)

### Attributes
- `[MappedFrom(typeof(Source))]` declares an attribute map. The previous attribute still works but is obsolete and hidden from IntelliSense.

### Documentation and packaging
- README links are absolute, so they work on nuget.org.
- README, package tags and benchmarks describe ConventionMapper on its own; the benchmarks compare with hand-written code and Mapster.

## 1.2.0 (2026-10-23)

### New package: ConventionMapper.SourceGenerator
- Build-time mapping: declare partial methods in a `[GeneratedMapper]` class and the generator writes plain C# mapping code at compile time. No reflection, no runtime dependency, Native AOT and trimming safe, debuggable.
- Same conventions as the runtime mapper: names, flattening (null-safe), `Get` methods, constructors and records, `init` members, nested objects (reusing your own mapping methods), recursive types, arrays, lists, sets, dictionaries, enums (by name, `[Flags]` by value), nullable, numeric and string conversions, update-in-place methods, extension methods.
- `[MapMember("Source.Path", "Destination")]` and `[MapIgnore("Destination")]`.
- Diagnostics CM0001-CM0008; unmapped destination members are build errors.

### Analyzers (in the ConventionMapper package)
- CM1001: unmapped destination members in `CreateMap<TSource, TDestination>();`, reported in the IDE while typing, with a code fix that adds `ForMember(..., o => o.Ignore())`.
- CM1002: suggests `Map<TSource, TDestination>` when the source type is known, with a code fix.

### Performance
- Nested maps and collections are compiled inline into the fast-path delegate: typed `for` loops over arrays and `List<T>`, struct enumerators for other sources, child maps expanded in place. Order with flattening and a 5-line list: 224 ns → 109 ns (Mapster 123 ns).
- `Condition`, `PreCondition` and `NullSubstitute` on directly assignable members are compiled instead of interpreted, and maps that need a context are called straight from the static cache without the engine's type-map lookup. A map with a condition: 192 ns → 69 ns.
- `ResolutionContext` is pooled per thread, so maps that need a context (resolvers, conditions, items) no longer allocate one per call (280 B → 80 B). `cfg.DisableContextPooling()` turns it off.
- The root type name used in error messages is only computed when an error occurs.

### Packaging
- Package validation against the latest published version for release builds.
- README renders correctly on nuget.org (no HTML in the heading).
- Benchmark workflow that commits BenchmarkDotNet results to the repository.

## 1.1.0 (2026-10-23)

### Performance
- Maps are compiled into a single strongly typed delegate on first use. Simple members (numbers, dates, enums, strings, flattened paths) are assigned directly with no boxing.
- Nested objects and collections (`List<T>`, `T[]`, `IEnumerable<T>`, `IList<T>`, `IReadOnlyList<T>`, ...) call the child map's compiled delegate directly instead of going through the engine.
- Static generic fast path for `Map<TSource, TDestination>(src)` and `Map<TDestination>(src)` (up to 8 source types per destination); maps made only of direct assignments run without a per-call context.
- `config.CompileMappings()` compiles everything up front; `cfg.UseCompiledMappings = false` turns compilation off.
- Fast-path failures are re-run on the full path, so errors still name the exact member.

### Features
- `ProjectTo` query parameters: `ProjectTo<PostDto>(config, new { currentUser = name })` replaces variables captured in `MapFrom` expressions (EF Core parameterizes them).
- `ProjectTo` explicit expansion: members configured with `ExplicitExpansion()` are only selected when requested, e.g. `ProjectTo<AuthorDto>(config, null, a => a.Books)`.
- `cfg.ValidateNullability = true` makes `AssertConfigurationIsValid()` report members whose source may be null while the destination is not nullable (`int?` → `int` everywhere; `string?` → `string` on .NET 6+).
- `Explain()` shows explicitly expanded members.

### Packaging and tooling
- Package icon, package validation, trimming / Native AOT annotations (`RequiresUnreferencedCode`, `RequiresDynamicCode`) on the configuration and DI entry points.
- CI runs the tests on Linux and on Windows, including .NET Framework 4.8 for the netstandard2.0 build.
- BenchmarkDotNet project comparing ConventionMapper with hand-written code and Mapster.
- ASP.NET Core + EF Core sample app.

## 1.0.0 (2026-10-23)

First release: convention-based mapping with flattening and unflattening, profiles, `ForMember` / `ForPath` / `ForCtorParam`, value resolvers, type and value converters, conditions, before/after actions, inheritance, constructor and record mapping, collections, dictionaries, enums, open generics, circular reference handling, attribute maps, `ProjectTo`, configuration validation, dependency injection, collection merging by key (`EqualityComparison`), PATCH mapping, strict enum mapping, immutable collections, full member paths in errors and `Explain()`.
