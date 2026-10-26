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

A parameter named exactly like the destination member still wins over a source member with that name, since the caller supplies it on purpose. In `[MapMember]` paths an exact-case match wins.

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

## Data-flow report

Each assembly with `[GeneratedMapper]` classes also gets `ConventionMapper.Generated.DataFlowReport` (internal): a Markdown report of every generated map, produced by the build so it always matches the code. For each mapper class it has:

* **Review first**: key-like members (`Id`, `CustomerId`, `[Key]`) written by convention, and parsing or narrowing conversions that are not allowed with `[MapAllowParse]`. Those rows are also marked ⚠.
* **Sensitive members**: every place a `[Sensitive]` member is copied to.
* **Conversion methods**: each one used, and the members it was applied to.
* A section per mapping method, with overloads under one heading, then the nested type pairs. Each row says where the destination member gets its value: matching name, flattened path, method parameter, conversion method, ignored or not listed.

`DataFlowReport.Markdown` is the whole assembly; `DataFlowReport.ByMapper["Shop.OrderMapper"]` is one class. Snapshot-test it so a new flow fails a test until a reviewer accepts it:

```csharp
[Fact]
public void Data_flows_are_reviewed() =>
    ConventionMapper.Generated.DataFlowReport.AssertMatchesDirectory("data-flow");
```

`AssertMatchesDirectory` keeps one file per mapper class (`data-flow/Shop.OrderMapper.md`), so pull requests that touch different maps don't conflict; `AssertMatches("data-flow.md")` keeps the whole report in one file. Relative paths start at the folder of the test file. A missing file is created and the test fails once, so it gets reviewed and committed; a changed file fails with a line diff; a file for a mapper that no longer exists fails as stale. Run the tests with `CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1` to rewrite the files. Line endings and trailing spaces are ignored, so `core.autocrlf` doesn't matter. The helpers need no test framework: a mismatch throws `DataFlowReport.SnapshotMismatchException`.

The runtime package has the same report for runtime maps: `config.DataFlowReport()`.

## Generated code in pull requests

Reviewers see attribute changes, not what they do. Keep the generated code in the repository to show the real diff:

```xml
<PropertyGroup>
  <ConventionMapperEmitGeneratedCode>Generated</ConventionMapperEmitGeneratedCode>
</PropertyGroup>
```

Every local build writes ConventionMapper's generated files into `Generated/` (relative to the project; a folder per target framework when the project has several) and removes files for mappers that are gone. CI builds (`ContinuousIntegrationBuild`, `CI=true` or `TF_BUILD`) don't write; they fail with error CM0100 when the committed copy is out of date. Override with `<ConventionMapperCheckGeneratedCode>true|false</ConventionMapperCheckGeneratedCode>`. The folder is not compiled; the compiler keeps using the generator's output.

For hover help in the IDE, `<ConventionMapperGeneratedDocs>true</ConventionMapperGeneratedDocs>` adds XML docs to each generated method listing where every member comes from. A doc comment you wrote on the declaration is kept, and the data flow goes into `<remarks>`.

## Testing mappers

Every mapper class gets `WriteSets`: the destination members each generated method writes, by `"Name(ParameterTypes)"` (as in the report) and by `"Name"` for methods without overloads. The [ConventionMapper.Testing](https://www.nuget.org/packages/ConventionMapper.Testing) package compares it with the real behavior:

```csharp
// Fills a LocationData with distinct values, maps it, and checks exactly which members got a value.
MapperAssert.WritesExactly((LocationData s) => s.ToImportData("GL1", 10m),
    Mappings.WriteSets["ToImportData(LocationData, string?, decimal?)"]);

// Fails when a mapping method is never called from the tests.
MapperAssert.AllMethodsCovered(typeof(Mappings), typeof(MappingTests).Assembly);
```

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

## IDE fixes

The package includes quick fixes (Ctrl+. in Visual Studio and Rider, Cmd+. in VS Code):

* **CM0001** (unmapped member): *Ignore 'X'*, *Ignore all N unmapped members*, and *Map only the mapped members ([MapExplicit])*, which lists the members the method maps today. In explicit mode: *Remove 'X' from [MapExplicit]*.
* **CM0020** (hidden): a member name written as a string in `[MapExplicit]`, `[MapIgnore]`, `[MapMember]` (destination), `[MapNullSubstitute]` or `[MapAllowParse]` becomes `nameof(OrderDto.Name)`, so a rename updates it. Fix all in a document, project or solution at once.
* **CM0023** (info): values assigned right after a mapping call (`var dto = order.ToDto(); dto.Note = note;`) can be parameters. The fix adds the overload `[MapInherit(nameof(ToDto))] OrderDto ToDto(this Order order, string? note)` to the mapper, or uses an existing one with those parameters, and passes the values to it.

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

The whole object is built in one place, and a caller that forgets a value gets a compile error instead of a silent default. A parameter takes priority over a source member with the same name, since the caller supplies it on purpose. In a `[MapMember]` path, though, a name that matches both a source member and a parameter (names are case-insensitive, so `"CreateUser"` matches `createUser`) is error CM0014; rename the parameter. A parameter that matches no destination member is error CM0011. Update methods take extra parameters after the target: `void Update(Dto source, Entity target, DateTime modifiedAt)`. Methods with extra parameters are not reused as conversions for nested members.

### Sharing configuration between methods

Overloads usually need the same lists. `[MapInherit]` reuses another method's `[MapExplicit]`, `[MapMember]`, `[MapIgnore]`, `[MapNullSubstitute]` and `[MapAllowParse]`:

```csharp
[MapExplicit(nameof(ImportData.LocationNumber), nameof(ImportData.Street1), nameof(ImportData.City))]
public static partial ImportData ToImportData(this LocationData source);

[MapInherit(nameof(ToImportData))]   // same lists, plus this overload's parameters
public static partial ImportData ToImportData(this LocationData source, string? glClassCode, decimal? exposure);
```

The inherited method must map the same source and destination types (a projection can inherit from a create method); among overloads, the one with the fewest parameters is used. The method's own attributes are added and win, and a parameter overrides an inherited `[MapIgnore]` for its member. A mismatch, an unknown name or a cycle is error CM0019.

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
| CM0008 | Warning | A mapping attribute (`[MapMember]`, `[MapIgnore]`, `[MapExplicit]`, `[MapNullSubstitute]`, `[MapAllowParse]`) names a member that does not exist. |
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
| CM0100 | Error | The generated code committed with `ConventionMapperEmitGeneratedCode` is out of date (CI builds). |

Severities can be changed in `.editorconfig`, for example `dotnet_diagnostic.CM0003.severity = warning`.

## Source generator or runtime mapper?

Use the generator for hot paths, Native AOT, trimming and when you want compile-time guarantees. Use the runtime `ConventionMapper` package for `ProjectTo` (LINQ / EF Core), DI-resolved resolvers and converters, `Patch`, collection merging by key and other configuration-driven features. Both packages can be used in the same project.
