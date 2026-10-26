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
| Numbers | implicit conversions directly, narrowing conversions in a `checked(...)` cast |
| Strings | `ToString()` from any scalar; `T.Parse(s, CultureInfo.InvariantCulture)` to any type with a `Parse` method |
| Nullable | `int?` ↔ `int` (`GetValueOrDefault()`), `int?` ↔ `long?` |
| Null collections | become empty collections |

Values of the same type are assigned as they are (reference types are not cloned).

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

## Your own conversions

Any static method in the mapper class that takes one value and returns another is used wherever those two types meet, and it takes priority over the built-in conversions:

```csharp
[GeneratedMapper]
public static partial class OrderMapper
{
    public static partial OrderDto ToDto(Order order);

    // Used for every Money -> string member, in every method of this mapper.
    private static string Format(Money money) => money.Amount.ToString("0.00") + " " + money.Currency;
}
```

Partial mapping methods are reused the same way for nested objects, so declaring `LineDto ToDto(Line line)` makes every `Line` → `LineDto` member (and list element) call it.

## Sensitive members

Mark members such as password hashes or tokens `[Sensitive]`. Copying one into a destination member that is not also `[Sensitive]` is build error CM0009, so it can't reach a DTO by accident. Projects that use only the generator get the attribute automatically. With the runtime `ConventionMapper` package installed too, its attribute is used.

## Data-flow report

Each assembly with `[GeneratedMapper]` classes also gets `ConventionMapper.Generated.DataFlowReport.Markdown` (internal), a Markdown report of every generated map: where each destination member gets its value, which members are ignored, and every place a `[Sensitive]` member is copied to. It is produced by the build, so it always matches the code. A common use is a snapshot test:

```csharp
[Fact]
public void Data_flows_are_reviewed()
{
    var expected = File.ReadAllText("data-flow.md");
    Assert.Equal(expected, ConventionMapper.Generated.DataFlowReport.Markdown);
}
```

A new flow of personal data then fails the test until `data-flow.md` is updated, which puts the change in front of a reviewer. The runtime package has the same report for runtime maps: `config.DataFlowReport()`.

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

## Configuring members

```csharp
[MapMember("Customer.Email", "ContactEmail")]   // source path -> destination member (or constructor parameter)
[MapIgnore("InternalNotes")]                    // leave a destination member out
public static partial OrderDto ToDto(Order order);
```

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
| CM0008 | Warning | `[MapMember]` / `[MapIgnore]` names a member that does not exist. |
| CM0010 | Warning | A projection reaches a type that contains itself; the recursive member is left null. |

Severities can be changed in `.editorconfig`, for example `dotnet_diagnostic.CM0003.severity = warning`.

## Source generator or runtime mapper?

Use the generator for hot paths, Native AOT, trimming and when you want compile-time guarantees. Use the runtime `ConventionMapper` package for `ProjectTo` (LINQ / EF Core), DI-resolved resolvers and converters, `Patch`, collection merging by key and other configuration-driven features. Both packages can be used in the same project.
