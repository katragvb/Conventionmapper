# ConventionMapper.SourceGenerator

Build-time object mapping. You declare partial mapping methods; the generator writes the implementation into your project when it compiles.

* **No reflection at runtime.** The output is plain C# assignments, loops and constructor calls.
* **Native AOT and trimming safe.** The package has no runtime dependency, not even on `ConventionMapper`. The repository's [AOT sample](https://github.com/katragvb/Conventionmapper/tree/main/samples/ConventionMapper.Sample.Aot) is published with `PublishAot=true` and warnings as errors, and run, on every CI build.
* **As fast as hand-written code.** There is no configuration lookup, no boxing, no delegate per member.
* **Debuggable.** Step into the generated method like any other code (in Visual Studio: *Go To Definition* on the method, or look under *Dependencies → Analyzers → ConventionMapper.SourceGenerator*).
* **Mistakes are build errors.** An unmapped destination member fails the build (`CM0001`) instead of showing up at runtime.

```
dotnet add package ConventionMapper.SourceGenerator
```

Requires the .NET 8 SDK or later (Visual Studio 2022 17.8+) and C# 9 or later. The generated code itself runs on every target framework, including .NET Framework and `netstandard2.0`.

## Declaring a mapper

```csharp
using ConventionMapper;

[GeneratedMapper]
public static partial class OrderMapper
{
    // TDestination Map(TSource): creates a new object
    public static partial OrderDto ToDto(this Order order);

    // void Map(TSource, TTarget): updates an existing object
    public static partial void Update(OrderDto source, Order target);

    // collections, enums and other types work at the root too
    public static partial List<OrderLineDto> ToDtos(IEnumerable<OrderLine> lines);
}

OrderDto dto = order.ToDto();
```

Mappers can be static or instance classes, nested inside other partial types, and methods can be extension methods. The attributes are added to your project by the generator and are removed from the compiled assembly.

## What gets mapped

| Feature | Example |
| --- | --- |
| Same name (case-insensitive) | `Name` → `Name` |
| Flattening, null-safe | `Customer.Address.City` → `CustomerAddressCity` (`source.Customer?.Address?.City`) |
| `Get` methods | `GetTotal()` → `Total` |
| Constructors and records | `record PersonDto(string FirstName, int Age)`, picking the constructor with the most parameters that can all be mapped |
| `init` and `required` members | set in the object initializer |
| Nested objects | a mapping method you declared for the pair is reused; otherwise a private helper is generated (recursive types work) |
| Collections | arrays, `List<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `HashSet<T>`, `ISet<T>`, with indexed `for` loops and pre-sized lists when the source has a count |
| Dictionaries | `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` (keys and values converted) |
| Enums | by name with a `switch`; `[Flags]` enums by value; enum ↔ string, enum ↔ integer |
| Numbers | implicit conversions directly, narrowing conversions in a `checked(...)` cast (warning CM0016, see below) |
| Strings | `ToString()` from any scalar; `T.Parse(s, CultureInfo.InvariantCulture)` to any type with a `Parse` method (warning CM0016) |
| Nullable | `int?` ↔ `int` (`GetValueOrDefault()`), `int?` ↔ `long?` |
| Null collections | become empty collections |

Values of the same type are assigned as they are (reference types are not cloned).

### Guesses are errors, risky conversions are warnings

The conventions never pick silently between two possible sources. Each of these is error CM0015, and `[MapMember]` chooses:

* a property and a `Get` method with the same name (`Total` and `GetTotal()`),
* two members whose names differ only in case (`Id` and `ID`),
* two flattenings (`BillingAddressCity` from `Billing.AddressCity` or `BillingAddress.City`),
* a method parameter and a flattened path (`customerName` and `Customer.Name`).

A parameter named exactly like the destination member still wins over a source member with that name, since the caller supplies it on purpose.

Conversions that can throw or lose data at runtime (`int.Parse`, `Enum.Parse`, a `checked` narrowing cast such as `long` → `int`) are warning CM0016. When the conversion is intended, say so for that member:

```csharp
[MapAllowParse("PostalCode", "Count")]   // on a method, or on the mapper class for all its methods
public static partial ImportRow ToRow(LegacyRow row);
```

A mapping method whose root is a conversion (`public static partial int Parse(string s)`) is not flagged: you asked for it.

## Database queries (EF Core projections)

Declare a projection and the generator writes a LINQ expression that EF Core (or any LINQ provider) translates to SQL, so only the columns the DTO needs are queried:

```csharp
[GeneratedMapper]
public static partial class OrderMapper
{
    // Use on any IQueryable: db.Orders.Where(...).ProjectToDto()
    public static partial IQueryable<OrderDto> ProjectToDto(this IQueryable<Order> query);

    // Or get the expression itself: db.Orders.Select(OrderMapper.Projection())
    public static partial Expression<Func<Order, OrderDto>> Projection();
}

var dtos = await db.Orders.Where(o => o.Status == OrderStatus.Shipped).ProjectToDto().ToListAsync();
```

The same conventions apply (names, flattening, nested objects, collections, enums, nullable values, constructors and records). The generated expression is created once and cached, which also lets EF Core cache the query. Within a projection:

* Navigation is plain (`x.Customer.Address.City`); the database handles null navigations.
* Nested objects are created inline with a null check, collections with `Select(...).ToList()` or `ToArray()`.
* Enums map by name with a conditional that becomes `CASE WHEN` in SQL.
* Your own static conversion methods are called as they are, so EF Core runs them after the query, on the client.
* A type that contains itself (a tree) cannot be expanded; that member is left null with warning CM0010.

Warning CM0021 marks the parts EF Core can't translate to SQL or evaluates on the client: your own conversion methods (allowed only in the final `Select`, never in a filter or sort), and `ToString()` on dates, floating-point numbers, decimals, booleans and enums, which the database formats differently from .NET. Name the provider to tune these checks:

```csharp
[GeneratedMapper(Provider = "SqlServer")]   // SqlServer, PostgreSql, Sqlite, Cosmos or InMemory
public static partial class OrderQueries { ... }
```

`InMemory` runs in .NET, so nothing is flagged; `Cosmos` also flags `ToString()` on integers and Guids. The generated expressions are the same for every provider (`??` becomes `COALESCE`, enum conditionals `CASE WHEN`). An unknown provider name is warning CM0022.

### Projections with parameters

A projection can take values, which become query parameters. Combine it with `[MapFrom]` to compute a member in SQL:

```csharp
[MapFrom(nameof(Selected), nameof(ProgramData.IsSelected))]
public static partial IQueryable<ProgramData> ProjectToProgramData(this IQueryable<Program> query, int selectedProgramId);

private static bool Selected(Program p, int selectedProgramId) => p.Id == selectedProgramId;

var programs = await db.Programs.ProjectToProgramData(selectedProgramId).ToListAsync();
// SELECT ..., CASE WHEN p.Id = @selectedProgramId THEN 1 ELSE 0 END AS IsSelected
```

A parameter named like a destination member sets it, as in other mapping methods. With parameters the expression is built per call (it can't be cached), and EF Core still caches the query plan because the values are parameters.

## EF Core entities

The generator reads your EF Core model from the source code, so scaffolded entities without attributes get accurate checks. It finds entity types through `DbSet<T>` properties, `IEntityTypeConfiguration<T>` classes, `modelBuilder.Entity<T>()` calls and the types their navigations reach (in this assembly or a referenced one), and reads `HasKey`, `HasForeignKey`, `Property(...).HasMaxLength`, `ValueGeneratedOnAdd` / `ValueGeneratedNever`, `UseIdentityColumn`, `HasDefaultValueSql` and `HasComputedColumnSql` from the fluent configuration. Attributes (`[Key]`, `[ForeignKey]`, `[MaxLength]`, `[StringLength]`, `[DatabaseGenerated]`) work too.

That information is used for:

* **Keys** in the "Review first" list of the [data-flow report](#data-flow-report).
* **Database-generated keys and navigations (CM0028, warning).** A map that writes an identity or other generated key, or a navigation property, into an entity: the most dangerous category when copying rows (duplicate keys, related rows attached to the new row). Leave the member out, or acknowledge it with `[MapReviewed]`. A single `int`, `long`, `short` or `Guid` key is generated by EF's conventions unless configured with `ValueGeneratedNever` or `[DatabaseGenerated(DatabaseGeneratedOption.None)]`.
* **Column lengths (CM0027).** See [Column lengths](#column-lengths).

The model is read from code, not from a running `DbContext`: configuration built in loops, through reflection or in another language isn't seen. When it matters, add the attribute to the entity.

### Copying one entity to another

Copying rows (a quote's locations into a new endorsement, an order into a template) is where mapping mistakes are most expensive. A pattern that holds up:

```csharp
[GeneratedMapper]
public static partial class EndorsementCopies
{
    // Only the listed columns are copied; a column added to both tables later is not copied silently.
    [MapExplicit(nameof(EndorsementLocation.LocationNumber), nameof(EndorsementLocation.Street1), nameof(EndorsementLocation.City))]
    [MapTruncate(nameof(EndorsementLocation.City))]
    [MapConstant(nameof(EndorsementLocation.Status), RowStatus.New)]
    [MapReviewed(nameof(EndorsementLocation.LocationNumber))]   // a natural key, copied on purpose
    public static partial EndorsementLocation ToEndorsementLocation(this QuoteLocation source, int endorsementId, int createUserId);
}

db.EndorsementLocations.AddRange(quote.Locations.Select(l => l.ToEndorsementLocation(endorsement.Id, user.Id)));
```

1. **Explicit mode.** `[MapExplicit]` names every column that is copied. Everything else keeps its default.
2. **Keys and audit columns as parameters.** The new row's foreign keys (`endorsementId`) and audit values (`createUserId`) come from the caller, so a forgotten one is a compile error. Constants such as a status use `[MapConstant]`.
3. **Never the row's own key.** Don't list `Id`; the database generates it. Listing it is warning CM0028.
4. **Navigation properties stay alone.** Don't list them. Copying `Quote` or `Locations` makes EF Core attach the source's related rows to the new row. Writing one is warning CM0028. Copy children with their own mapping method and add them explicitly.
5. **Mark deliberate key copies.** A natural key that should carry over gets `[MapReviewed]`, so the report says `key (reviewed)` instead of listing it under "Review first".
6. **Test it.** `MapperAssert.WritesExactly` checks the columns, `MapperAssert.WritesFrom` with the generated lineage checks each comes from the right source column (see [Testing mappers](#testing-mappers)).

Existing hand-written copies can be converted with the CM0029 fix; see [Migrating hand-written copies](#migrating-hand-written-copies).

## Your own conversions

A static method in the mapper class that takes one value and returns another is used wherever those two types meet, and it takes priority over the built-in conversions. Mark it `[MapConversion]` to say that is the intent:

```csharp
[GeneratedMapper]
public static partial class OrderMapper
{
    public static partial OrderDto ToDto(Order order);

    // Used for every Money -> string member, in every method of this mapper.
    [MapConversion]
    private static string Format(Money money) => money.Amount.ToString("0.00") + " " + money.Currency;
}
```

A one-argument helper that was never meant as a conversion (`static int ParseCode(string s)`) would otherwise be applied to every `string` → `int` member, so an unmarked method that gets used is warning CM0017, which names the members it was applied to. With `[GeneratedMapper(RequireMapConversion = true)]` only marked methods are used at all. `[MapConversion]` on a method that cannot convert (not static, or not exactly one parameter) is error CM0018. The data-flow report lists every conversion method and the members it was applied to.

Partial mapping methods are reused the same way for nested objects, so declaring `LineDto ToDto(Line line)` makes every `Line` → `LineDto` member (and list element) call it.

## Sensitive members

Mark members such as password hashes or tokens `[Sensitive]`. Copying one into a destination member that is not also `[Sensitive]` is build error CM0009, so it can't reach a DTO by accident. Projects that use only the generator get the attribute automatically. With the runtime `ConventionMapper` package installed too, its attribute is used.

Members a map copies whose names look personal (`Ssn`, `TaxId`, `DateOfBirth`, `Email`, `Phone`, `BankAccount`, `Iban`, `Passport`, `Password`, `ApiKey` and similar, also with Hungarian prefixes such as `StrSsn` or `DtDateOfBirth`) and that aren't marked yet are info CM0026, with the fix *Mark [Sensitive]*. Only strings and birth dates are suggested. Raise it to a warning in `.editorconfig` to require a decision for each one.

### Redacted copies and log text

The same metadata keeps logs clean:

```csharp
[MapRedact]
public static partial Person Redact(Person person);         // a copy with [Sensitive] members masked

[MapLogString]
public static partial string ToLogString(Person person);    // "Person { Id = 7, Name = Ann, Ssn = *** }"

logger.LogInformation("Saved {Person}", PersonMapper.ToLogString(person));
```

`Redact` copies the object and masks every `[Sensitive]` member, in nested objects and collections too: strings become `"***"`, other values their default. `ToLogString` writes the members as text with `[Sensitive]` values shown as `***`. Neither is used as a conversion for other maps.

### Diffs

A method returning `IReadOnlyList<FieldChange>` compares two objects member by member, with the same matching as a map from the first type to the second (including `[MapMember]` and `[MapIgnore]`):

```csharp
internal static partial IReadOnlyList<FieldChange> Diff(Policy before, Endorsement after);

foreach (var change in PolicyMapper.Diff(policy, endorsement))
    audit.Add(change.Member, change.Before, change.After);   // ToString(): "City: Leeds -> York"
```

`[Sensitive]` values are reported as `"***"`. `FieldChange` is internal; a public method can return `IReadOnlyList<(string Member, object? Before, object? After)>` instead.

## Data-flow report

Each assembly with `[GeneratedMapper]` classes also gets `ConventionMapper.Generated.DataFlowReport` (internal): a Markdown report of every generated map, produced by the build so it always matches the code. For each mapper class it has:

* **Review first**: keys written by convention, and parsing or narrowing conversions that are not allowed with `[MapAllowParse]`. Those rows are also marked ⚠. A key is a member marked `[Key]` or `[ForeignKey]`, named by `[PrimaryKey]` or a navigation's `[ForeignKey]`, or, when the destination is an EF Core entity (a `DbSet<T>` or `IEntityTypeConfiguration<T>` in this or a referenced assembly, and the types their navigations reach), a key by EF's conventions: `Id`, `<Type>Id`, `<Navigation>Id`, `<PrincipalType>Id`. A DTO's `Id` or a name like `ErrorObjectId` alone is not a key. Once reviewed, `[MapReviewed("PolicyId")]` on the method (or class) takes a key off the list; its row then says `key (reviewed)`.
* **Sensitive members**: every place a `[Sensitive]` member is copied to.
* **Conversion methods**: each one used, and the members it was applied to.
* **Source members no map reads**: members of a mapped source type that no method copies. That's either dead data or a forgotten mapping ("`QuoteLocation.TivTotal` is never copied": intended, or a bug?).
* **Maps to `X` side by side**: when several methods write the same destination type (`ReferralData` from quote referrals and from endorsement referrals), a table of which members each one sets, so an asymmetry such as "quote referrals don't set `CreateUserId`" stands out.
* A section per mapping method, with overloads under one heading, then the nested type pairs. Each row says where the destination member gets its value: matching name, flattened path, method parameter, constant, computed by a method, conversion method, ignored or not listed.

`DataFlowReport.Markdown` is the whole assembly; `DataFlowReport.ByMapper["Shop.OrderMapper"]` is one class. Snapshot-test it so a new flow fails a test until a reviewer accepts it:

```csharp
[Fact]
public void Data_flows_are_reviewed() =>
    ConventionMapper.Generated.DataFlowReport.AssertMatchesDirectory("data-flow");
```

`AssertMatchesDirectory` keeps one file per mapper class (`data-flow/Shop.OrderMapper.md`), so pull requests that touch different maps don't conflict; `AssertMatches("data-flow.md")` keeps the whole report in one file. Relative paths start at the folder of the test file. A missing file is created and the test fails once, so it gets reviewed and committed; a changed file fails with a line diff; a file for a mapper that no longer exists fails as stale. Run the tests with `CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1` to rewrite the files. Line endings and trailing spaces are ignored, so `core.autocrlf` doesn't matter. The helpers need no test framework: a mismatch throws `DataFlowReport.SnapshotMismatchException`.

A failing snapshot also describes the change in plain English above the diff, ready to paste into a pull request:

```
Changes:
- ToEndorsementLocation(QuoteLocation) now also writes TaxCode (from TaxCode)
- ToDto(Order) now writes Buyer from Customer.Name instead of Customer.Email
- OrderDto lost the member Legacy (API change)
```

Removed and added members of destination types are listed, so a breaking change to a DTO stands out in review.

For other tools, the same flows are available as `DataFlowReport.Csv` (one row per source member → destination member), `DataFlowReport.Json`, and `DataFlowReport.Mermaid`, a flowchart in which chained maps show column → DTO → API lineage (useful for privacy requests such as "where does the SSN go?" and impact analysis before a schema change). Flows of `[Sensitive]` members are drawn as thick arrows.

The runtime package has the same report for runtime maps: `config.DataFlowReport()`.

## Generated code in pull requests

Reviewers see attribute changes, not what they do. Keep the generated code in the repository to show the real diff:

```xml
<PropertyGroup>
  <ConventionMapperEmitGeneratedCode>Generated</ConventionMapperEmitGeneratedCode>
</PropertyGroup>
```

Every local build writes ConventionMapper's generated files into `Generated/` (relative to the project; a folder per target framework when the project has several), removes files for mappers that are gone, and says so in the build log. CI builds don't write; they fail with error CM0100 when the committed copy is out of date. The folder is not compiled; the compiler keeps using the generator's output.

> **Make sure your CI checks.** CI is detected from `ContinuousIntegrationBuild=true` or the environment variables `CI`, `TF_BUILD`, `GITHUB_ACTIONS`, `GITLAB_CI`, `JENKINS_URL`, `BUILDKITE`, `TEAMCITY_VERSION`, `CIRCLECI`, `BITBUCKET_BUILD_NUMBER`, `CODEBUILD_BUILD_ID` and `HARNESS_BUILD_ID`. Other systems (Concourse, for one) set none of these: build there with `-p:ConventionMapperCheckGeneratedCode=true`, or set `CI=true` in the task. Otherwise the build silently rewrites the files instead of failing; the "ConventionMapper updated the generated code" line in the build log shows it happened.

For hover help in the IDE, `<ConventionMapperGeneratedDocs>true</ConventionMapperGeneratedDocs>` adds XML docs to each generated method listing where every member comes from. A doc comment you wrote on the declaration is kept, and the data flow goes into `<remarks>`.

## Testing mappers

Every mapper class gets `WriteSets`: the destination members each generated method writes, by `"Name(ParameterTypes)"` (as in the report) and by `"Name"` for methods without overloads. The [ConventionMapper.Testing](https://www.nuget.org/packages/ConventionMapper.Testing) package compares it with the real behavior:

```csharp
// Fills a LocationData with distinct values, maps it, and checks exactly which members got a value.
MapperAssert.WritesExactly((LocationData s) => s.ToImportData("GL1", 10m),
    Mappings.WriteSets["ToImportData(LocationData, string?, decimal?)"]);

// Fails when a mapping method is never called from the tests.
MapperAssert.AllMethodsCovered(typeof(Mappings), typeof(MappingTests).Assembly);

// Stricter: every create and update method must run inside WritesExactly / UpdatesExactly.
MapperAssert.AllMethodsVerified(typeof(Mappings), typeof(MappingTests).Assembly);
```

String keys break when a signature changes. `WriteSetOf` looks a method up by name and parameter types instead: a rename is a compile error, and a changed signature fails with the current overloads.

```csharp
Mappings.WriteSetOf(nameof(Mappings.ToImportData), typeof(LocationData), typeof(string), typeof(decimal?));
```

`Lineage` (and `LineageOf`, by name and parameter types) records which source member each destination member is copied from. The Testing package uses it to check where values come from, not just which members are written, which catches swapped pairs such as `SubmitUserId` filled from `CompleteUser.Id`:

```csharp
MapperAssert.WritesFrom((Claim c) => c.ToDto(), MapperAssert.LineageOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Claim)));

// Every create and update method, run with sample data and compared with its WriteSets and Lineage.
MapperAssert.AllMappersMatchWriteSets(typeof(Mappings));

// A map and its reverse, also with 100 randomized inputs (nulls, empty and long strings, extremes).
MapperAssert.RoundTrips<Person, PersonDto>(Mappings.ToDto, Mappings.ToPerson, runs: 100);

// Mapping methods no production code uses: candidates for deletion.
MapperAssert.AllMethodsUsed(typeof(Mappings), typeof(OrderService).Assembly);
```

See the [ConventionMapper.Testing README](https://github.com/katragvb/Conventionmapper/blob/main/src/ConventionMapper.Testing/README.md) for sample-data options (valid values for a member, enum subsets, factories).

## Moving a runtime map to the generator

With the `ConventionMapper` package installed, the IDE offers **Generate build-time mapper for S → D** on any `CreateMap<S, D>()` call (Ctrl+. in Visual Studio and Rider, Cmd+. in VS Code). It adds a new file with the equivalent mapper class:

```csharp
// CreateMap<Order, OrderDto>().ForMember(d => d.Buyer, o => o.MapFrom(s => s.Customer.Name)).ReverseMap();
[GeneratedMapper]
public static partial class OrderMapper
{
    [MapMember("Customer.Name", "Buyer")]
    public static partial OrderDto ToOrderDto(this Order source);

    public static partial Order ToOrder(this OrderDto source);
}
```

`Ignore()`, `MapFrom` with a member path and `ReverseMap()` are translated. Other options (conditions, resolvers, converters) are listed in a comment at the top of the file so you can convert them by hand, usually as a static conversion method in the same class. Once the project builds, remove the `CreateMap` call.

## Migrating hand-written copies

Info CM0029 marks object initializers that copy at least three same-name members from one object, and conversion operators whose body is such a copy:

```csharp
public static implicit operator OrderDto(Order order) => new OrderDto
{
    Id = order.Id, Name = order.Name, Total = order.Total,          // same name
    Label = order.Title, City = order.Address.City,                 // other members or paths
    Status = Status.New,                                            // a constant
    Code = order.Code.Length > 3 ? order.Code.Substring(0, 3) : order.Code,   // anything else
};
```

*Convert to generated mapping* replaces the copy with a call to a new mapping method, in the file's `[GeneratedMapper]` class or a new `OrderDtoMappings` class next to the type:

```csharp
public static implicit operator OrderDto(Order order) =>
    OrderDtoMappings.ToOrderDto(order, code: order.Code.Length > 3 ? order.Code.Substring(0, 3) : order.Code);

[GeneratedMapper]
public static partial class OrderDtoMappings
{
    [MapExplicit(nameof(OrderDto.Id), nameof(OrderDto.Name), nameof(OrderDto.Total))]
    [MapMember("Title", nameof(OrderDto.Label))]
    [MapMember("Address.City", nameof(OrderDto.City))]
    [MapConstant(nameof(OrderDto.Status), Status.New)]
    public static partial OrderDto ToOrderDto(Order source, string code);
}
```

The new method is in explicit mode, so it sets exactly the members the hand-written code set. Values the attributes can't express become parameters, so their expressions stay at the call site unchanged; members of a different type that need a conversion (a user-defined operator, say) are passed the same way. Two differences to be aware of: a null source returns null instead of throwing, and a null in a flattened path (`order.Address.City`) gives null instead of a `NullReferenceException`.

For an operator without such leftover values, *Replace the operator with a generated mapping (update call sites)* also removes the operator and rewrites every implicit conversion and cast in the solution to call the method. When a use can't be rewritten (a `foreach` that converts its elements), the operator is kept and delegates to the method.

Copies inside expression trees (EF Core `Select` lambdas and query syntax) are not reported, since a method call there would not translate; declare a projection instead. The threshold is configurable in `.editorconfig`:

```ini
[*.cs]
conventionmapper.copy_min_members = 5
# List every candidate in the build output, for a migration overview per project:
dotnet_diagnostic.CM0029.severity = warning
```

## IDE fixes

The package includes quick fixes (Ctrl+. in Visual Studio and Rider, Cmd+. in VS Code):

* **CM0001** (unmapped member): *Ignore 'X'*, *Ignore all N unmapped members*, and *Map only the mapped members ([MapExplicit])*, which lists the members the method maps today. In explicit mode: *Remove 'X' from [MapExplicit]*.
* **CM0020** (hidden): a member name written as a string in `[MapExplicit]`, `[MapIgnore]`, `[MapMember]` (destination), `[MapNullSubstitute]` or `[MapAllowParse]` becomes `nameof(OrderDto.Name)`, so a rename updates it. Fix all in a document, project or solution at once.
* **CM0023** (info): values assigned right after a mapping call (`var dto = order.ToDto(); dto.Note = note;`) can be parameters. The fix adds the overload `[MapInherit(nameof(ToDto))] OrderDto ToDto(this Order order, string? note)` to the mapper, or uses an existing one with those parameters, and passes the values to it.
* **CM0026** (info): a personal-looking member that isn't `[Sensitive]`: *Mark [Sensitive]*.
* **CM0029** (info): a hand-written copy or conversion operator: *Convert to generated mapping*, and for operators *Replace the operator with a generated mapping (update call sites)*. See [Migrating hand-written copies](#migrating-hand-written-copies).

## Configuring members

```csharp
[MapMember("Customer.Email", "ContactEmail")]   // source path -> destination member (or constructor parameter)
[MapIgnore("InternalNotes")]                    // leave a destination member out
[MapNullSubstitute("Status", "")]               // value used when the source is null
public static partial OrderDto ToDto(Order order);
```

### Values from the caller (extra parameters)

When some destination members come from the caller rather than the source object, add them as parameters. A parameter sets the destination member (or constructor parameter) with the same name, and a `[MapMember]` path can start at a parameter:

```csharp
[MapMember("policy.Number", "PolicyNumber")]
public static partial ReferralGap ToReferralGap(this Referral referral, int submissionNumber, Policy? policy);

var gap = referral.ToReferralGap(submissionNumber, policy);
```

The whole object is built in one place, and a caller that forgets a value gets a compile error instead of a silent default. A parameter takes priority over a source member with the same name, since the caller supplies it on purpose.

In a `[MapMember]` path, names are case-insensitive, so `"CreateUser"` could mean the source member `CreateUser` or a parameter `createUser`. The exact case decides: `"CreateUser"` is the source member, `"createUser"` the parameter. A path that starts with the source parameter's name, such as `"source.CreateUser"`, always means the source. Only a path that matches both, in neither's exact case, is error CM0014. A parameter that matches no destination member is error CM0011. Update methods take extra parameters after the target: `void Update(Dto source, Entity target, DateTime modifiedAt)`. Methods with extra parameters are not reused as conversions for nested members.

### Sharing configuration between methods

Overloads usually need the same lists. `[MapInherit]` reuses another method's `[MapExplicit]`, `[MapMember]`, `[MapIgnore]`, `[MapNullSubstitute]` and `[MapAllowParse]`:

```csharp
[MapExplicit(nameof(ImportData.LocationNumber), nameof(ImportData.Street1), nameof(ImportData.City))]
public static partial ImportData ToImportData(this LocationData source);

[MapInherit(nameof(ToImportData))]   // same lists, plus this overload's parameters
public static partial ImportData ToImportData(this LocationData source, string? glClassCode, decimal? exposure);
```

The inherited method must map the same source and destination types (a projection can inherit from a create method). When more than one overload qualifies, name the one to inherit from by its parameter types:

```csharp
[MapInherit(nameof(ToImportData), typeof(LocationData))]
public static partial IQueryable<ImportData> Project(IQueryable<LocationData> query);
```

The method's own attributes are added and win, and a parameter overrides an inherited `[MapIgnore]` for its member. A mismatch, an ambiguous or unknown name, or a cycle is error CM0019.

`nameof` works in every mapping attribute, and keeps member names correct through renames; the CM0020 fix converts existing strings.

### Explicit mode

By default every destination member with a matching source member is copied, so a column added later to both types is copied without anyone noticing. `[MapExplicit]` turns this around: only the members you name are set, and every other member keeps its default value.

```csharp
[MapExplicit("Id", "Name", "Status")]          // copied by name
[MapMember("Customer.Email", "ContactEmail")]  // MapMember entries are included
public static partial OrderRow ToRow(Order order, DateTime createdAt);  // so are parameters
```

Listed members still need a source (CM0001), and a listed name that does not exist is CM0008, so a rename doesn't silently drop a member. No `[MapIgnore]` lists are needed. On the mapper class, `[MapExplicit]` applies to every method; a method can list more names with its own `[MapExplicit(...)]`. Nested objects mapped through generated helpers still use the conventions; declare a mapping method with `[MapExplicit]` for a nested pair to make it explicit too.

### Null substitutes

`[MapNullSubstitute("Status", "")]` writes `source.Status ?? ""`, and in a projection `COALESCE(Status, '')`. The value must be a constant (string, number, `bool`, `char` or enum) that converts implicitly to the member type. A substitute for a source that can never be null is warning CM0012; a constant of the wrong type is error CM0013.

### Constants and computed values

Parameters that only ever receive a constant, and values the caller computes from the source, belong in the mapping:

```csharp
[MapConstant(nameof(Row.PolicyTypeCode), 1)]
[MapConstant(nameof(Row.Status), FormStatus.New)]
[MapFrom(nameof(IsManuscript), nameof(Row.Manuscript))]      // one member, computed by a method
[MapFrom(nameof(ContextOrNew), nameof(Row.ContextId))]
public static partial Row ToRow(Program program);

private static bool IsManuscript(Program p) => p.Manuscript == "1";
private static Guid ContextOrNew(Program p) => p.ContextId ?? Guid.NewGuid();
```

`[MapConstant]` takes a constant that converts implicitly to the member type. `[MapFrom]` names a static method of the mapper class that takes the source and, after it, any of the mapping method's extra parameters by name; it applies to that one member only, unlike a `[MapConversion]`. In a projection an expression-bodied `[MapFrom]` method is inlined, so it runs in SQL. Both count as listed in explicit mode. A value of the wrong type, or a method with the wrong signature, is error CM0025.

### Required members

`[MapRequired(nameof(Row.ProgramId))]` makes the map throw `InvalidOperationException` ("ToRow: Row.ProgramId is required, but Program.ProgramId is null.") when the source is null, instead of writing a default. It is the opposite of `[MapNullSubstitute]`, for values that must exist, so a member can't have both. Projections can't throw, so it isn't allowed there (filter the query instead); a source that can never be null is CM0025 too.

### Column lengths

A string copied into a member with a maximum length (`[MaxLength]`, `[StringLength]` or `HasMaxLength` in the EF model) from a source that can be longer is CM0027: a warning when the destination is an EF Core entity (the insert would fail or cut the value), info otherwise. `[MapTruncate]` cuts the value to fit, or `[MapReviewed]` acknowledges it:

```csharp
[MapTruncate(nameof(EndorsementLocation.City))]       // to the destination's maximum length
[MapTruncate(nameof(EndorsementLocation.County), 30)] // or to a given length
public static partial EndorsementLocation ToEndorsementLocation(QuoteLocation source);
```

In projections the cut is a `Substring`, which translates to SQL.

### Merging collections

`[MapMergeKey]` turns `void Merge(IEnumerable<TSource>, ICollection<TTarget>)` into a reconciliation by key: matching items are updated in place (with the mapper's `Update(TSource, TTarget)` method when it has one, so its `[MapIgnore]` lists apply), new items are created (with its create method when it has one) and added, and target items without a match are removed:

```csharp
[MapMergeKey(nameof(CoverageInput.ContextId))]                  // or (sourceKey, targetKey) when the names differ
public static partial void Merge(IEnumerable<CoverageInput> source, ICollection<Coverage> target);

[MapMergeKey(nameof(CoverageInput.ContextId), RemoveMissing = false)]   // keep unmatched target items
public static partial void MergeKeep(IEnumerable<CoverageInput> source, ICollection<Coverage> target);
```

With an EF Core navigation collection as the target, removed items are deleted (or orphaned, depending on the relationship) on `SaveChanges`. The two keys must have the same type, or one must convert implicitly to the other.

## Diagnostics

| Id | Severity | Meaning |
| --- | --- | --- |
| CM0001 | Error | A destination member has no matching source member. Add `[MapMember]` or `[MapIgnore]`. |
| CM0002 | Error | A source member exists but its type cannot be converted to the destination type. |
| CM0003 | Info | A nullable source is mapped onto a non-nullable destination; null becomes the default value. |
| CM0004 | Error | The destination has no accessible constructor whose parameters can all be mapped. |
| CM0005 | Error | The mapper class (and every class containing it) must be `partial`. |
| CM0006 | Error | The method signature is not a supported mapping method. |
| CM0007 | Warning | A source enum value has no destination value with the same name; mapping it throws. |
| CM0009 | Error | A `[Sensitive]` source member would be copied into a destination member that is not `[Sensitive]`. Add `[MapIgnore]`, or mark the destination `[Sensitive]`. |
| CM0008 | Warning | A mapping attribute (`[MapMember]`, `[MapIgnore]`, `[MapExplicit]`, `[MapNullSubstitute]`, `[MapAllowParse]`, `[MapReviewed]`, `[MapConstant]` and the others) names a member that does not exist. |
| CM0010 | Warning | A projection reaches a type that contains itself; the recursive member is left null. |
| CM0011 | Error | An extra parameter of a mapping method does not match any destination member. |
| CM0012 | Warning | `[MapNullSubstitute]` on a member whose source can never be null. |
| CM0013 | Error | The `[MapNullSubstitute]` value is not a constant that converts to the member type. |
| CM0014 | Error | A `[MapMember]` source path matches both a source member and a method parameter. |
| CM0015 | Error | A destination member could come from more than one source (property and `Get` method, names differing in case, two flattenings, or a parameter and a flattening). Choose with `[MapMember]`. |
| CM0016 | Warning | A parsing or narrowing conversion can throw or lose data. Allow it with `[MapAllowParse]`. |
| CM0017 | Warning | A static method not marked `[MapConversion]` is used as a conversion. |
| CM0018 | Error | `[MapConversion]` on a method that cannot convert. |
| CM0019 | Error | `[MapInherit]` names an unknown method, one with other types, or forms a cycle. |
| CM0020 | Hidden | A member name in a mapping attribute can use `nameof` (with a fix). |
| CM0021 | Warning | Part of a projection may not translate to SQL or runs on the client. |
| CM0022 | Warning | Unknown `Provider` in `[GeneratedMapper]`. |
| CM0023 | Info | Members assigned right after a mapping call can be parameters (with a fix). |
| CM0024 | Warning | The IDE runs an older generator than the package the project references. Restart the IDE or its C# language server. |
| CM0025 | Error | A `[MapConstant]`, `[MapFrom]`, `[MapRequired]`, `[MapTruncate]`, `[MapMergeKey]`, `[MapRedact]` or `[MapLogString]` doesn't fit the member or method (wrong type, unknown method, wrong signature). |
| CM0026 | Info | A member that looks like personal data is copied and isn't `[Sensitive]` (with a fix). |
| CM0027 | Warning / Info | A string can be longer than the destination allows (warning for EF Core entities). Use `[MapTruncate]` or `[MapReviewed]`. |
| CM0028 | Warning | A map writes a database-generated key or a navigation property into an EF Core entity. |
| CM0029 | Info | A hand-written copy or conversion operator can be a generated mapping (with fixes). |
| CM0100 | Error | The generated code committed with `ConventionMapperEmitGeneratedCode` is out of date (CI builds). |

Severities can be changed in `.editorconfig`, for example `dotnet_diagnostic.CM0003.severity = warning`.

## Troubleshooting

**Errors such as "the call is ambiguous" right after upgrading the package.** The IDE keeps the previous generator loaded, so the generated code is from the old version. Restart Visual Studio or Rider, or in VS Code run *.NET: Restart Language Server*. From 1.9.0 on, warning CM0024 says when this happens. A command-line `dotnet build` always uses the new version.

**CM0100 never fires in CI.** Your CI system isn't detected; see the note under [Generated code in pull requests](#generated-code-in-pull-requests).

## Source generator or runtime mapper?

Use the generator for hot paths, Native AOT, trimming and when you want compile-time guarantees. Use the runtime `ConventionMapper` package for DI-resolved resolvers and converters, `Patch` and other configuration-driven features. Both packages can be used in the same project.
