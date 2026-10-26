# Changelog

## 1.8.0 (unreleased)

Changes from migrating a large codebase (GARS) to the source generator.

**Upgrading:** projects with `TreatWarningsAsErrors` may see new warnings fail the build: mark intended conversion methods `[MapConversion]` (CM0017), allow intended parsing with `[MapAllowParse]` (CM0016), and re-record data-flow report snapshots (new format). New ambiguity errors (CM0015) point at bindings that were guesses before.

### Source generator: silent-wrong-code guards
- **Ambiguous bindings are errors (CM0015).** A destination member that matches a property and a `Get` method (`Total` / `GetTotal()`), two members differing only in case, two flattenings (`Billing.AddressCity` / `BillingAddress.City`), or a parameter and a flattened path (`customerName` / `Customer.Name`) used to bind silently to one of them. Choose with `[MapMember]`. A parameter still wins over a source member with the destination's own name. Explicit paths now prefer an exact-case match.
- **Parsing and narrowing conversions are warnings (CM0016).** `int.Parse`, `Enum.Parse` and `checked` narrowing casts (`long` → `int`) can throw or lose data at runtime. Allow them per member with `[MapAllowParse("Code")]` on the method or class. A root conversion method (`int Map(string)`) is not flagged.
- **Conversion-method trap (CM0017).** A one-argument static method in a `[GeneratedMapper]` class is still used as a conversion wherever its types meet, but now warns until it is marked `[MapConversion]`. `[GeneratedMapper(RequireMapConversion = true)]` uses only marked methods. A `[MapConversion]` method of the wrong shape is error CM0018.
- **Error CM0014** (from the unreleased 1.7.1): a `[MapMember]` source path that matched both a source member and a method parameter (case-insensitively, e.g. `"CreateUser"` and `createUser`) was bound to the parameter, which could generate wrong code such as `int.Parse(createUser)`.

### Source generator: shared configuration
- **`[MapInherit(nameof(ToImportData))]`** reuses another method's `[MapExplicit]`, `[MapMember]`, `[MapIgnore]`, `[MapNullSubstitute]` and `[MapAllowParse]`, for example on an overload with extra parameters. The method's own attributes are added and win; a parameter overrides an inherited `[MapIgnore]` for its member. Different source or destination types, an unknown name or a cycle is error CM0019. Projections can inherit from create methods.

### Source generator: projections
- **Warning CM0021** for projection parts EF Core can't translate to SQL or runs on the client: custom conversion methods, and `ToString()` on dates, floating-point numbers, decimals, booleans and enums.
- **`[GeneratedMapper(Provider = "SqlServer")]`** (also `PostgreSql`, `Sqlite`, `Cosmos`, `InMemory`) tunes those warnings to the provider: none for `InMemory`, more for `Cosmos`. The generated expressions are the same for every provider. Unknown names are warning CM0022.

### Data-flow report
- **Sections per mapping method**, with overloads grouped under one heading and nested type pairs under "Nested maps". **This changes the report format**: re-record existing snapshots once.
- **"Review first"** lists key-like members (`Id`, `CustomerId`, `[Key]`) written by convention and unallowed parsing or narrowing conversions; such rows are marked ⚠.
- **"Conversion methods"** lists every conversion method used and the members it was applied to; rows note `via Format()`.
- **`DataFlowReport.ByMapper`**: the report of each mapper class on its own.
- **Snapshot helpers** generated into the assembly: `DataFlowReport.AssertMatches("data-flow.md")` (one file) and `DataFlowReport.AssertMatchesDirectory("data-flow")` (one file per mapper class, so PRs touching different maps don't conflict). Paths are relative to the calling test file; a missing file is created; a mismatch fails with a line diff; `CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1` re-records; line endings and trailing spaces are ignored.

### Generated code in pull requests
- **`<ConventionMapperEmitGeneratedCode>Generated</ConventionMapperEmitGeneratedCode>`** writes the generated mapping code into the project folder on every local build, and fails CI builds (`ContinuousIntegrationBuild`, `CI=true` or `TF_BUILD`) with CM0100 when the committed copy is out of date. Reviewers see code diffs instead of attribute diffs.
- **`<ConventionMapperGeneratedDocs>true</ConventionMapperGeneratedDocs>`** adds XML docs to generated methods listing each member's source, shown on hover. A declaration's own doc comment is kept and the data flow goes into `<remarks>`.

### Testing
- **`WriteSets`** on every mapper class: the members each generated method writes, by `"Name(ParameterTypes)"` (and `"Name"` without overloads).
- **New package `ConventionMapper.Testing`**: `MapperAssert.WritesExactly` fills a source object and checks exactly which members a map writes (against a list or a generated write set); `MapperAssert.UpdatesExactly` for update methods; `MapperAssert.AllMethodsCovered(typeof(Mappings), testAssembly)` fails when a mapping method has no test; `SampleData.Create<T>()`. No test-framework dependency.

### IDE
- CM0001 offers "Ignore 'X'", "Ignore all N unmapped members" and "Map only the mapped members ([MapExplicit])"; in explicit mode "Remove 'X' from [MapExplicit]".
- Hidden CM0020 with a fix (also for a whole document, project or solution) that turns member-name strings in mapping attributes into `nameof(Type.Member)`, so renames are refactor-safe.
- CM0023 (info) on `var x = s.ToY(); x.A = ...; x.B = ...;` after a generated mapping. The fix adds an overload `[MapInherit(nameof(ToY))] ToY(S s, A a, B b)` (or reuses one) and passes the values to it.
- The source generator package now contains these fixes.

### Packages
- Every package contains `CHANGELOG.md`, and its release notes are the section for its version, so the NuGet UI shows what changed.

## 1.7.0 (2026-10-26)

### Data-flow reports
- `config.DataFlowReport()` returns a Markdown report of every map: where each destination member gets its value (matching name, flattened path, computed, ignored, not mapped) and every place a `[Sensitive]` member is copied to. Useful for code reviews and privacy audits.
- The source generator writes the same report for the `[GeneratedMapper]` classes of each assembly into `ConventionMapper.Generated.DataFlowReport.Markdown`, so it is known at build time.

### Source generator: safer mappings
- **Extra parameters.** `Gap ToGap(this Referral r, int submissionNumber, Policy? policy)`: extra parameters set the destination members (or constructor parameters) with the same name, and `[MapMember("policy.Number", "PolicyNumber")]` can start at a parameter. The object is built in one place and a missing value is a compile error. Works for update methods too. An unused parameter is error CM0011.
- **Explicit mode.** `[MapExplicit("Id", "Name")]` on a method, or `[MapExplicit]` on the mapper class, sets only the listed members, `[MapMember]` entries and parameters; everything else keeps its default. A member added later is never copied silently, and no `[MapIgnore]` lists are needed.
- **Null substitutes.** `[MapNullSubstitute("Status", "")]` writes `source.Status ?? ""` (`COALESCE` in projections). Warning CM0012 when the source can't be null, error CM0013 for a constant of the wrong type.

### "Generate build-time mapper" refactoring
- On a `CreateMap<TSource, TDestination>()` call, the IDE offers to add a new file with the equivalent `[GeneratedMapper]` class. `Ignore()`, `MapFrom(s => s.A.B)`, `MapFrom("A.B")`, `NullSubstitute(constant)` and `ReverseMap()` are translated; anything else is listed at the top of the file to convert by hand.

### Tests
- The IDE quick fixes (CM1001, CM1002) and the refactoring are tested in a Roslyn workspace.

## 1.6.0 (2026-10-24)

### Source generator: database projections
- `IQueryable<TDest> M(this IQueryable<TSource> query)` and `Expression<Func<TSource, TDest>> M()` generate a cached LINQ expression that EF Core translates to SQL, selecting only the columns the DTO needs. Flattening, nested objects, collections, enums (as `CASE WHEN`), nullable values and record constructors are supported. Tested against SQLite through EF Core in CI.
- New warning CM0010 when a projection reaches a recursive type.

### Project
- `CONTRIBUTING.md`, `SECURITY.md` (private vulnerability reporting), issue and pull request templates, Dependabot for GitHub Actions and NuGet.

## 1.5.0 (2026-10-23)

### Data-leak guard
- `[Sensitive]` marks members (password hashes, tokens, personal data) that must not be copied into a destination member that is not `[Sensitive]` too.
- Enforced while typing (analyzer CM1003, with a fix that ignores the member), at build time in generated mappers (CM0009) and at startup (`MapperConfigurationException`).
- Covers matching names, flattening, `MapFrom` member paths, string paths, constructor parameters and `ForPath`; derived values are allowed.

### Change tracking
- `mapper.MapWithChanges(source, destination)` maps into an existing object and returns every changed value with its path, old value and new value (nested members, collection elements and counts). `[Sensitive]` values are redacted. An overload takes per-call options, e.g. `IgnoreNullSourceValues()` for PATCH.

## 1.4.0 (2026-10-23)

### Performance
- `Map<TSource, TDestination>` calls a strongly typed `Func<TSource, TDestination>` from the static cache: no casts, no boxing and no try/catch inside the compiled code. Flat 8-property map: 16.0 ns → 13.9 ns (Mapster 16.0 ns).

### Source generator
- Static methods declared in a `[GeneratedMapper]` class are used as conversions wherever their types occur, and take priority over the built-in conversions: `private static string Format(Money m) => ...` formats every `Money` → `string` member.

### Native AOT
- New sample `samples/ConventionMapper.Sample.Aot`, published with `PublishAot=true` and warnings as errors, and run in CI on every push.

## 1.3.0 (2026-10-23)

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
