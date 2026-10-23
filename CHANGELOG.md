# Changelog

## 1.1.0 (unreleased)

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
- BenchmarkDotNet project comparing ConventionMapper with hand-written code, Mapster and AutoMapper.
- ASP.NET Core + EF Core sample app and an AutoMapper migration guide.

## 1.0.0 (2026-10-23)

First release: convention-based mapping with flattening and unflattening, profiles, `ForMember` / `ForPath` / `ForCtorParam`, value resolvers, type and value converters, conditions, before/after actions, inheritance, constructor and record mapping, collections, dictionaries, enums, open generics, circular reference handling, attribute maps, `ProjectTo`, configuration validation, dependency injection, collection merging by key (`EqualityComparison`), PATCH mapping, strict enum mapping, immutable collections, full member paths in errors and `Explain()`.
