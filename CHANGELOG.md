# Changelog

## 1.9.0 (unreleased)

Fixes from adopting 1.8.0 in a large codebase, plus values, EF Core awareness, data protection, migration tooling and behavior tests.

**Upgrading:**
- `[MapInherit]` naming a method with several matching overloads is now error CM0019 (1.8.0 silently took the one with the fewest parameters); add the parameter types: `[MapInherit(nameof(ToDto), typeof(Order))]`.
- New warnings can appear on existing maps: CM0028 (a map writes a database-generated key or a navigation into an EF Core entity) and CM0027 (a string can be longer than an entity column allows). Both point at real risks; fix them, or acknowledge each with `[MapReviewed]`. With warnings as errors, check before upgrading.
- Re-record data-flow snapshots (`CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1`): plain `Id` / `…Id` names are no longer flagged as keys, and the report has new sections.

### Source generator
- **"Review first" lists only real keys.** A member is a key when it is marked `[Key]` or `[ForeignKey]`, named by `[PrimaryKey]` or a navigation's `[ForeignKey]`, or, when the destination is an EF Core entity (a `DbSet<T>` on a `DbContext` or an `IEntityTypeConfiguration<T>`, in this assembly or a referenced one, plus the types their navigations reach), a key by EF's conventions (`Id`, `<Type>Id`, `<Navigation>Id`, `<PrincipalType>Id`). Names alone (`ErrorObjectId`, `RecordID`, `ReadByUserId` on a DTO) no longer count.
- **`[MapReviewed("PolicyId")]`** on a method or the mapper class takes reviewed keys off the list (the row says `key (reviewed)`). It is inherited through `[MapInherit]`; an unknown name is CM0008.
- **Exact case decides `[MapMember]` sources.** `"CreateUser"` binds to the source member and `"createUser"` to a parameter with that name, so parameters keep natural names. A path starting with the source parameter's name (`"source.CreateUser"`) always means the source. CM0014 remains for paths that match both with neither in exact case.
- **`[MapInherit(nameof(ToDto), typeof(Order), ...)]`** picks an overload by its parameter types; several matching overloads without them is error CM0019, which suggests the attribute to write.
- **`WriteSetOf(nameof(ToDto), typeof(Order))`** on every mapper class: the write set by method name and parameter types. A rename is a compile error, a changed signature throws with the current overloads.
- **CM0024 (warning):** the IDE is still running an older generator after a package upgrade (the cause of confusing errors such as ambiguous calls). Restart the IDE or its C# language server. Works for upgrades from 1.9.0 on.
- Generated files always use LF line endings (the data-flow report mixed CRLF and LF on Windows).
- The CM0023 fix names the parent overload by its parameter types when it is overloaded.

### Values
- **`[MapConstant("Status", FormStatus.New)]`**: a constant for a member, instead of a parameter that only ever receives one.
- **`[MapFrom(nameof(Compute), "Member")]`**: one member computed by a static method that takes the source (and any extra parameters by name). Expression-bodied methods are inlined in projections, so they run in SQL.
- **`[MapRequired("ProgramId")]`**: throws a descriptive `InvalidOperationException` when the source is null, instead of writing a default.
- **`[MapTruncate("City")]`** cuts a string to the destination's maximum length (`[MaxLength]`, `[StringLength]`, the EF model, or a given length). **CM0027** marks strings that can be longer than the destination allows (warning for EF Core entities, info otherwise).
- **Projections with parameters**: `IQueryable<ProgramData> ProjectToProgramData(this IQueryable<Program> q, int selectedProgramId)`; the values are query parameters.
- **`[MapMergeKey(nameof(Input.ContextId))]`** on `void Merge(IEnumerable<S>, ICollection<T>)`: updates matching items in place, adds new ones, removes the rest (`RemoveMissing = false` keeps them).
- CM0025: a value attribute that doesn't fit its member or method.

### EF Core awareness
- The generator reads the EF Core model from source (`DbSet<T>`, `IEntityTypeConfiguration<T>`, `modelBuilder.Entity<T>()`, and fluent `HasKey`, `HasForeignKey`, `HasMaxLength`, `ValueGenerated*`, `UseIdentityColumn`, `HasDefaultValueSql`, `HasComputedColumnSql`), so scaffolded entities without attributes get accurate keys, lengths and generated values.
- **CM0028 (warning):** a map writes a database-generated key or a navigation property into an entity.
- New docs section: copying one entity to another (explicit mode, keys as parameters, navigations left alone).

### Data protection
- **CM0026 (info):** a copied member whose name looks personal (`Ssn`, `TaxId`, `DateOfBirth`, `Email`, `Phone`, `BankAccount`, also with prefixes such as `StrSsn`) isn't `[Sensitive]`. The fix *Mark [Sensitive]* adds the attribute.
- **`[MapRedact] T Redact(T value)`**: a copy with every `[Sensitive]` member masked, deeply.
- **`[MapLogString] string ToLogString(T value)`**: log-safe text, `Person { Id = 7, Ssn = *** }`.
- **Generated diffs:** `IReadOnlyList<FieldChange> Diff(Policy before, Endorsement after)` (or a tuple list for public methods), with the same member matching as a map; `[Sensitive]` values show as `***`. Useful for audit trails.

### Data-flow report
- **Source members no map reads**: dead data, or a forgotten mapping.
- **Maps to `X` side by side**: methods that write the same destination type, compared member by member.
- A failing snapshot describes the change in plain English ("ToEndorsementLocation now also writes TaxCode"), including removed and added DTO members marked as API changes.
- `DataFlowReport.Csv`, `.Json` and `.Mermaid` exports: column-level lineage for privacy requests and impact analysis; `[Sensitive]` flows are drawn as thick arrows.
- Generated `Lineage` and `LineageOf(nameof(M), typeof(...))` on every mapper class: destination member → the source path it is copied from.

### Migration
- **CM0029 (info):** object initializers and conversion operators that copy three or more same-name members by hand (threshold `conventionmapper.copy_min_members`). *Convert to generated mapping* creates an explicit-mode mapping method (`[MapExplicit]`, `[MapMember]`, `[MapConstant]`, parameters for the rest) and calls it; for operators, *Replace the operator with a generated mapping (update call sites)* removes the operator and rewrites its implicit and explicit uses across the solution. Raise the severity to warning to list every candidate in the build output.

### Generated code in the repository
- CI is also detected from `GITHUB_ACTIONS`, `GITLAB_CI`, `JENKINS_URL`, `BUILDKITE`, `TEAMCITY_VERSION`, `CIRCLECI`, `BITBUCKET_BUILD_NUMBER`, `CODEBUILD_BUILD_ID` and `HARNESS_BUILD_ID`. Systems that set none of them (Concourse) need `-p:ConventionMapperCheckGeneratedCode=true`.
- A build that rewrites the generated files says so in the build log, so a CI system that isn't detected is visible.

### ConventionMapper.Testing
- **`MapperAssert.AllMethodsVerified`**: every create and update method must run inside a `WritesExactly` / `UpdatesExactly` check (projections must be called), so a test that calls a map without asserting no longer counts.
- **`MapperAssert.WriteSetOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Order))`**: typed write-set lookup.
- **`MapperAssert.WritesFrom`**: checks each destination value came from the expected source member, with the generated lineage (`MapperAssert.LineageOf`). Catches swapped pairs and names the member the value really came from.
- **`MapperAssert.AllMappersMatchWriteSets(typeof(Mappings))`**: runs every generated create and update method with sample data and compares it with its write set and lineage. A zero-effort baseline for new maps.
- **`MapperAssert.RoundTrips(to, back)`**: deep comparison of `back(to(x))` with `x`; `runs: 100` uses randomized inputs (nulls, empty and long strings, extremes) and reports the failing seed.
- **`MapperAssert.AllMethodsUsed(typeof(Mappings), productionAssemblies)`**: maps no production code uses.
- **`SampleDataOptions`**: per-type and per-member values, enum subsets, factories for interfaces and types without a usable constructor, collection size, depth, and a seeded random mode. `SampleData` also uses private parameterless constructors.

## 1.8.0 (2026-10-26)

Changes from migrating a large codebase to the source generator.

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
