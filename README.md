# ConventionMapper

[![NuGet](https://img.shields.io/nuget/v/ConventionMapper.svg)](https://www.nuget.org/packages/ConventionMapper)
[![Downloads](https://img.shields.io/nuget/dt/ConventionMapper.svg)](https://www.nuget.org/packages/ConventionMapper)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/katragvb/Conventionmapper/blob/main/LICENSE)

A fast, convention-based object-object mapper for .NET. It removes the boring code that copies one object into another (entities → DTOs, view models, API contracts) with conventions, profiles and compiled mapping code.

Targets **.NET Standard 2.0** (works on .NET Framework 4.6.1+, .NET Core, Unity, Xamarin) and **.NET 8+**.

```
dotnet add package ConventionMapper
```

## Why ConventionMapper

* **Convention first.** Matching names, flattening, `Get` methods, constructors and records map with no configuration. Profiles, `CreateMap`, `ForMember`, `ReverseMap`, `ProjectTo`, `AssertConfigurationIsValid` and DI registration cover everything else.
* **Data-leak guard.** Mark fields such as password hashes or tokens `[Sensitive]`, and any mapping that would copy them into a DTO fails at build time and at startup. See [Data-leak guard](#data-leak-guard-sensitive).
* **Know what changed.** `mapper.MapWithChanges(dto, entity)` updates an object and returns every changed value with its old and new value, ready for audit logs. See [Change tracking](#change-tracking-mapwithchanges).
* **Build-time code generation.** The `ConventionMapper.SourceGenerator` package writes plain C# mapping code into your project at compile time: no reflection, Native AOT and trimming safe, as fast as hand-written code, and unmapped members are build errors. See [Build-time mapping](#build-time-mapping-source-generator).
* **Mistakes caught while typing.** A bundled analyzer flags unmapped members in `CreateMap<,>()` in the IDE, with one-click fixes. See [Analyzers](#analyzers).
* **Compiled for speed.** Every map is compiled into a single typed delegate with direct, non-boxing assignments, and simple maps skip per-call overhead entirely through a static generic cache. See [Performance](#performance).
* **More built in.** Collection merging by key, PATCH mapping, strict enum validation, immutable collections, full-path error messages and `Explain()` ship in the core package, with no add-ons. See [Exclusive built-in features](#exclusive-built-in-features).
* **MIT licensed.** Free for every use, commercial included, with no license key.

## Performance

### How it works

1. **One compiled delegate per map.** The first time a map is used, it is compiled into a strongly typed expression tree. `Person → PersonDto` becomes the equivalent of `new PersonDto { Id = src.Id, Name = src.Name, ... }`.
2. **Direct assignments, no boxing.** Numbers, dates, enums, strings and flattened paths such as `Customer.Address.City` are copied directly, with null-safe navigation. Conditions, pre-conditions and null substitutes compile to plain `if` checks. Only members that genuinely need more (resolvers, custom conversions) call into the engine, member by member, so results are identical to the interpreter.
3. **Nested maps and collections inlined.** On the fast path, child maps are expanded in place and collections are filled by typed loops (`for` over arrays and `List<T>`, the type's own struct enumerator otherwise), with no per-element delegate calls and no boxed enumerators. Existing destination objects and collections are reused exactly as by the interpreter.
4. **Zero-overhead fast path.** Maps made only of direct assignments and compiled child maps run without a per-call context object. `Map<TSource, TDestination>(src)` calls a strongly typed `Func<TSource, TDestination>` held in a static generic field: no dictionary lookup, no casts, no boxing and no try/catch inside the compiled code. Maps that do need a context are called from the same cache, skipping the engine's type-map lookup. If a fast-path call fails, it is re-run on the full path, so the exception still names the exact member.
5. **Pooled context.** Maps that need a `ResolutionContext` (resolvers, conditions, per-call items) reuse one per thread instead of allocating one per call. Call `cfg.DisableContextPooling()` if a resolver keeps a reference to the context after `Map` returns.
6. **Automatic fallback.** Maps that need reference tracking (cycles, `PreserveReferences`, `MaxDepth`), struct destinations and `Patch()` calls run on the interpreter, with the same behaviour.

```csharp
var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(Program).Assembly));
config.CompileMappings();          // optional: compile everything at startup instead of on first use

cfg.UseCompiledMappings = false;   // optional: always use the interpreter
```

### Benchmarks

The repository includes a [BenchmarkDotNet](https://benchmarkdotnet.org/) project that compares ConventionMapper (runtime and source-generated) with hand-written code and Mapster 7.4:

* **Flat:** `Person → PersonDto`, 8 properties (int, string, DateTime, bool, decimal).
* **Nested:** `Order → OrderDto` with flattening (`CustomerAddressCity`) and a list of 5 lines.
* **Context:** `Person → PersonDto` with a `Condition` that reads the `ResolutionContext`.

```
dotnet run -c Release --project benchmarks/ConventionMapper.Benchmarks -- --filter *
```

Results from the repository's benchmark workflow (BenchmarkDotNet 0.13.12, .NET 8.0, AMD EPYC 9V45 GitHub Actions runner; raw reports in [`benchmarks/results`](https://github.com/katragvb/Conventionmapper/tree/main/benchmarks/results)). Lower is better.

| Scenario | Hand-written | ConventionMapper | ConventionMapper (generated) | Mapster 7.4 |
| --- | ---: | ---: | ---: | ---: |
| Flat: 8 properties | 7.8 ns | 13.9 ns | **7.9 ns** | 16.0 ns |
| Nested: flattening + list of 5 | 79.6 ns¹ | 61.9 ns | **52.7 ns** | 69.4 ns |

¹ The hand-written nested version uses `Select(...).ToList()`; the mappers use pre-sized lists and indexed loops.

A map with a `Condition` (which needs a `ResolutionContext`) runs in 36.1 ns. Allocations equal hand-written code for flat maps (80 B) and Mapster for nested maps (384 B). Context pooling cuts the per-call allocation of context-dependent maps from 192 B to 80 B. Numbers vary by machine; run the suite yourself with the command above.

### Tips for the fastest mapping

* Prefer `mapper.Map<TSource, TDestination>(src)` in hot paths: it hits the static cache with a single type check.
* Call `config.CompileMappings()` at startup to move compilation cost out of the first request.
* Keep hot maps simple: convention matches and `MapFrom(s => s.A.B)` paths compile to direct assignments, while delegates, resolvers and conditions go through the engine.
* For database queries, use `ProjectTo<TDto>()` so only the needed columns are selected.

## Build-time mapping (source generator)

```
dotnet add package ConventionMapper.SourceGenerator
```

```csharp
[GeneratedMapper]
public static partial class OrderMapper
{
    [MapMember("Customer.Email", "ContactEmail")]
    [MapIgnore("InternalNotes")]
    public static partial OrderDto ToDto(this Order order);

    public static partial void Update(OrderDto source, Order target);

    // EF Core: translated to SQL, only the needed columns are selected
    public static partial IQueryable<OrderDto> ProjectToDto(this IQueryable<Order> query);
}

OrderDto dto = order.ToDto();
List<OrderDto> shipped = await db.Orders.Where(o => o.Shipped).ProjectToDto().ToListAsync();
```

The generator implements the partial methods at compile time with the same conventions as the runtime mapper (names, flattening, `Get` methods, constructors and records, nested objects, collections, dictionaries, enums, nullable and numeric conversions). Static methods in the mapper class, such as `private static string Format(Money m)`, are used for every member of those types. Extra method parameters supply values from the caller (`ToGap(this Referral r, int submissionNumber)`), `[MapExplicit("Id", "Name")]` copies only the members you list so new columns are never copied silently, and `[MapNullSubstitute("Status", "")]` replaces null sources. Projection methods (`IQueryable<TDest> M(IQueryable<TSource>)` or `Expression<Func<TSource, TDest>> M()`) produce expressions that EF Core translates to SQL, tested against a real database in CI. The result is ordinary C# you can step through, with no runtime dependency, so it works with Native AOT and trimming. An unmapped destination member is a build error (`CM0001`). The [Native AOT sample](https://github.com/katragvb/Conventionmapper/tree/main/samples/ConventionMapper.Sample.Aot) is published with `PublishAot=true` and run on every CI build. See [docs/source-generator.md](https://github.com/katragvb/Conventionmapper/blob/main/docs/source-generator.md).

## Analyzers

The `ConventionMapper` package ships a Roslyn analyzer with code fixes:

* **CM1001:** `CreateMap<TSource, TDestination>();` has a destination member with no matching source member. Shown in the IDE while typing instead of when `AssertConfigurationIsValid()` runs. The fix adds `.ForMember(d => d.X, o => o.Ignore())`.
* **CM1002:** `mapper.Map<TDestination>(source)` where the source type is known. The fix rewrites it to the faster `Map<TSource, TDestination>(source)`.
* **CM1003 (error):** `CreateMap<,>()` would copy a `[Sensitive]` member into a member that isn't. The fix ignores the member.

* **Generate build-time mapper (refactoring):** on any `CreateMap<TSource, TDestination>()` call, adds a file with the equivalent `[GeneratedMapper]` class, translating `Ignore`, `MapFrom` member paths and `ReverseMap`, so moving a map to the source generator takes one click.

Make CM1001 a build error with `dotnet_diagnostic.CM1001.severity = error` in `.editorconfig`. See [docs/analyzers.md](https://github.com/katragvb/Conventionmapper/blob/main/docs/analyzers.md).

## Data-leak guard ([Sensitive])

Mark members that must never reach an API response, a log or a client:

```csharp
public class User
{
    public string Email { get; set; }
    [Sensitive] public string PasswordHash { get; set; }
    [Sensitive] public string ApiToken { get; set; }
}

public class UserDto
{
    public string Email { get; set; }
    public string PasswordHash { get; set; }   // matches by name...
}

cfg.CreateMap<User, UserDto>();
// ...so the configuration fails:
// User -> UserDto: 'PasswordHash' would receive the [Sensitive] member 'PasswordHash'.
```

The guard covers every way a value is copied as is: matching names, flattening (`Profile.ApiKey` → `ProfileApiKey`), `MapFrom(s => s.PasswordHash)`, string paths, constructor parameters and `ForPath`. Values computed from a sensitive member, such as `MapFrom(s => s.PasswordHash != null)`, are allowed.

It is checked in three places:

* **While typing:** analyzer **CM1003** marks the `CreateMap<,>()` call as an error, with a fix that ignores the member.
* **At build time:** the source generator reports **CM0009** and does not generate the copy.
* **At startup:** `new MapperConfiguration(...)` throws `MapperConfigurationException`, for maps the analyzer cannot see.

To allow a copy on purpose (for example into a cache entry that is protected too), mark the destination member `[Sensitive]` as well. Sensitive data can only flow into sensitive members.

### Data-flow report

For code reviews and privacy audits, get every flow of data through the configuration as Markdown:

```csharp
File.WriteAllText("data-flow.md", config.DataFlowReport());
```

```
## Sensitive members

| Member | Copied to |
| --- | --- |
| `User.PasswordHash` | not copied anywhere |
| `User.ApiToken` | `CacheEntry.ApiToken` (protected) |

### `Order` → `OrderDto`

| Destination | Source | Notes |
| --- | --- | --- |
| CustomerName | `Customer.Name` | flattened |
| Total | `GetTotal()` |  |
| InternalNotes | — | ignored |
```

The source generator writes the same report for generated mappers at build time, in `ConventionMapper.Generated.DataFlowReport.Markdown`; a unit test can save it, or compare it with a checked-in copy so any new flow of personal data shows up in the pull request.

## Change tracking (MapWithChanges)

Update an existing object and get back exactly what changed:

```csharp
MappingChanges changes = mapper.MapWithChanges(dto, entity);

if (changes.HasChanges)
{
    auditLog.Write(changes.ToString());
    // Email: "a@x.com" -> "b@x.com"; Address.City: "London" -> "Paris"; Lines[1].Quantity: 1 -> 5; Lines.Count: 2 -> 3
    await db.SaveChangesAsync();
}

foreach (var change in changes)
    Console.WriteLine($"{change.Path}: {change.OldValue} -> {change.NewValue}");
```

* Paths cover nested objects (`Address.City`), collection elements (`Lines[1].Quantity`) and sizes (`Lines.Count`). Collections of simple values are compared as a whole (`Codes: [1, 2] -> [1, 2, 3]`).
* `changes.Find("Email")` returns one change, and `changes.Changed("Address")` checks a member and everything below it.
* Members marked `[Sensitive]` are reported as changed, but their values are never exposed: `ApiToken: (sensitive value changed)`.
* Per-call options work as usual, so `mapper.MapWithChanges(patch, entity, opt => opt.IgnoreNullSourceValues())` gives a PATCH that also reports what it changed.

## Built-in features

These ship in the core package, with no add-on packages or hand-written workarounds:

| Feature | How |
| --- | --- |
| Data-leak guard | `[Sensitive]` members can't be copied into unprotected destinations |
| Change tracking | `mapper.MapWithChanges(source, destination)` returns old and new values |
| Build-time generated mappers (no reflection, Native AOT) | `ConventionMapper.SourceGenerator` package |
| Unmapped members reported while typing | Bundled analyzer (CM1001) with a code fix |
| Update collections by key (EF Core) | `EqualityComparison(...)` |
| Strict, validated enum mapping | `ConvertUsingEnumMapping(...)` |
| PATCH mapping (skip null source values) | `mapper.Patch(dto, entity)` |
| Readable description of a map | `Explain()` returns plain text |
| Precise error location | Full path with collection indexes, e.g. `OrderDto.Lines[3].Product` |
| Immutable collection destinations | `ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableDictionary<K,V>`, ... with no extra dependency |
| License | MIT, free for any use |

### Update collections by key (EF Core friendly)

```csharp
cfg.CreateMap<TagDto, TagEntity>().EqualityComparison((dto, entity) => dto.Id == entity.Id);

mapper.Map(postDto, trackedPostEntity);
// matching tags are updated in place, new ones added, missing ones removed;
// the collection instance itself is kept, so EF Core change tracking works
```

### PATCH mapping (ignore null source values)

```csharp
mapper.Patch(patchDto, entity);                       // per call
cfg.CreateMap<UserPatch, User>().IgnoreNullSourceValues(); // per map
cfg.IgnoreNullSourceValues = true;                     // per profile / globally
mapper.Map(dto, entity, opt => opt.IgnoreNullSourceValues());
```

Only members whose source value is non-null are written, so a DTO with nullable properties can update just the fields a client sent.

### Strict, validated enum mapping

```csharp
cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o
    .MapByName()                          // or MapByValue(), MapByName(ignoreCase: true)
    .MapValue(Color.Purple, ColorDto.Violet)
    .Fallback(ColorDto.Unknown));         // optional
```

Without a `Fallback`, any source value that has no match is reported by `AssertConfigurationIsValid()`, and mapping it at runtime throws instead of silently producing a wrong value. Enum maps also work inside `ProjectTo`.

### Immutable collections

`ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableHashSet<T>`, `ImmutableSortedSet<T>`, `ImmutableQueue<T>`, `ImmutableStack<T>`, `ImmutableDictionary<K,V>`, `ImmutableSortedDictionary<K,V>` and their `IImmutable*` interfaces are supported as destinations, with no extra dependency.

### Errors that point at the exact member

```
Error mapping types.

Mapping types:
String -> Int32

Path:
ParentDto.Items[1].Count
```

`MappingException.MemberPath` holds the same path for logging.

### Explain a map

```csharp
Console.WriteLine(config.Explain<Order, OrderDto>());
```

```
Order -> OrderDto   (profile 'OrderProfile')
  Construction: parameterless constructor
  Members:
    Id                   <- Id
    CustomerName         <- Customer.Name
    CustomerAddressCity  <- Customer.Address.City
    Lines                <- Lines   [List<OrderLine> -> OrderLineDto[], items via map OrderLine -> OrderLineDto]
    Status               <- Status   [OrderStatus -> OrderStatusDto]
    Total                <- GetTotal()
    Notes                <- Notes   [null substitute: (none)]
```

### Nullability checks

```csharp
cfg.ValidateNullability = true;
config.AssertConfigurationIsValid();
// LooseSource -> StrictDest, member 'Score': source 'Score' may be null ('Score' is nullable)
// but the destination is not nullable. Add NullSubstitute(...), a Condition, Ignore(), or make the destination nullable.
```

With `ValidateNullability` on, validation also reports members whose source can be null while the destination cannot, before they silently become `0`, `default` or `null` at runtime. Nullable value types (`int?` → `int`) are checked on every framework; nullable reference annotations (`string?` → `string`) are checked on .NET 6 and later.

## Getting started

```csharp
using ConventionMapper;

var configuration = new MapperConfiguration(cfg =>
{
    cfg.CreateMap<Order, OrderDto>();
    cfg.CreateMap<OrderLine, OrderLineDto>();
});

// Only during development / in a unit test: fail fast on unmapped members
configuration.AssertConfigurationIsValid();

var mapper = configuration.CreateMapper();
OrderDto dto = mapper.Map<OrderDto>(order);
```

### With dependency injection

```csharp
services.AddConventionMapper(cfg =>
{
    cfg.CreateMap<Foo, FooDto>();
}, typeof(Program)); // scans the assembly containing Program
```

This registers `MapperConfiguration` / `IConfigurationProvider` (singleton) and `IMapper` (transient). Every `Profile` and every `[MappedFrom]` class in the scanned assemblies is added, and these implementations are registered as transient services so they can take constructor dependencies:

`IValueResolver<,,>`, `IMemberValueResolver<,,,>`, `ITypeConverter<,>`, `IValueConverter<,>`, `ICondition<,,>`, `IPreCondition<,>`, `IMappingAction<,>`.

`AddConventionMapper` can be called more than once; configuration accumulates.

## Profiles

```csharp
public class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>()
            .ForMember(d => d.Notes, o => o.NullSubstitute("(none)"))
            .ReverseMap();
    }
}

var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(OrderProfile).Assembly));
```

## Conventions

| Feature | Example |
| --- | --- |
| Same name (case-insensitive) | `Name` → `Name` |
| Flattening | `Customer.Address.City` → `CustomerAddressCity` |
| `Get` methods | `GetTotal()` → `Total` |
| Constructor / record mapping | `record PersonDto(string FirstName, int Age)` |
| Unflattening via `ReverseMap()` | `CustomerName` → `Customer.Name` |
| Collections | arrays, `List<T>`, `IEnumerable<T>`, `ICollection<T>`, `HashSet<T>`, `ReadOnlyCollection<T>`, `ObservableCollection<T>` |
| Dictionaries | `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` (keys and values mapped) |
| Enums | enum ↔ enum (by name, then value), string ↔ enum, number ↔ enum |
| Primitive conversions | `IConvertible`, static `Parse`, implicit/explicit operators, `TypeConverter` |
| Nullable | `int` ↔ `int?` |
| Null collections | become empty collections (set `AllowNullCollections = true` to keep null) |

Naming conventions, prefixes and postfixes:

```csharp
cfg.SourceMemberNamingConvention = LowerUnderscoreNamingConvention.Instance; // first_name → FirstName
cfg.RecognizePrefixes("Str");
cfg.RecognizeDestinationPostfixes("Dto");
cfg.ReplaceMemberName("Ä", "A");
cfg.AddGlobalIgnore("Internal");
```

## Member configuration

```csharp
cfg.CreateMap<Person, PersonDto>()
    .ForMember(d => d.FullName,  o => o.MapFrom(s => s.FirstName + " " + s.LastName))
    .ForMember(d => d.Secret,    o => o.Ignore())
    .ForMember(d => d.Age,       o => o.Condition(s => s.Age >= 0))
    .ForMember(d => d.Nickname,  o => o.NullSubstitute("n/a"))
    .ForMember(d => d.Code,      o => o.MapFrom("Details.Code"))            // string path
    .ForMember(d => d.Total,     o => o.MapFrom<TotalResolver>())           // IValueResolver
    .ForMember(d => d.Name,      o => o.ConvertUsing(new UpperConverter(), s => s.LastName)) // IValueConverter
    .ForMember(d => d.Tag,       o => o.MapFrom((s, d, m, ctx) => ctx.Items["Tag"]))
    .ForPath(d => d.Address.City, o => o.MapFrom(s => s.City))
    .ForCtorParam("id",          o => o.MapFrom(s => s.Key))
    .ForSourceMember(s => s.Password, o => o.DoNotValidate())
    .BeforeMap((s, d) => { /* ... */ })
    .AfterMap<AuditAction>();                                                // IMappingAction
```

Other options: `PreCondition`, `UseDestinationValue`, `SetMappingOrder`, `AddTransform`, `ForAllMembers`, `IgnoreAllPropertiesWithAnInaccessibleSetter`, `ConstructUsing`, `MaxDepth`, `PreserveReferences`, `IncludeMembers`, `DisableCtorValidation`.

Validate against the source side instead of the destination side with `CreateMap<A, B>(MemberList.Source)`, or turn validation off for a map with `MemberList.None`.

## Type converters

```csharp
cfg.CreateMap<string, DateTime>().ConvertUsing(s => DateTime.Parse(s));
cfg.CreateMap<DateTime, string>().ConvertUsing<DateTimeToStringConverter>();
cfg.CreateMap(typeof(Source<>), typeof(Dest<>)).ConvertUsing(typeof(MyGenericConverter<,>));
```

## Inheritance

```csharp
cfg.CreateMap<Shape, ShapeDto>()
   .Include<Circle, CircleDto>()
   .Include<Square, SquareDto>();
cfg.CreateMap<Circle, CircleDto>();
cfg.CreateMap<Square, SquareDto>();

List<ShapeDto> dtos = mapper.Map<List<ShapeDto>>(shapes); // CircleDto / SquareDto instances
```

`IncludeBase<TBaseSource, TBaseDest>()` works the other way round. Member configuration and before/after actions are inherited from base maps.

## Open generics

```csharp
cfg.CreateMap(typeof(Page<>), typeof(PageDto<>));
var dto = mapper.Map<PageDto<UserDto>>(page);
```

## Circular references

Maps that can reach themselves (trees, parent/child links) automatically preserve references, so cycles do not cause a stack overflow and shared instances stay shared. Use `MaxDepth(n)` to cut a graph at a fixed depth instead.

## Attributes

```csharp
[MappedFrom(typeof(Person), ReverseMap = true)]
public class PersonDto
{
    public string FirstName { get; set; }
    [SourceMember("LastName")] public string Surname { get; set; }
    [IgnoreMap] public string Secret { get; set; }
}
```

Attribute maps are picked up by `AddMaps(...)` and by `AddConventionMapper(...)` assembly scanning.

## Mapping into an existing object

```csharp
mapper.Map(source, existingDestination);   // updates in place; nested objects and collections are reused
```

## Per-call options

```csharp
mapper.Map<OrderDto>(order, opt =>
{
    opt.Items["UserId"] = userId;          // available as ctx.Items in resolvers / converters
    opt.AfterMap((src, dest) => { /* ... */ });
});
```

## Value transformers

```csharp
cfg.ValueTransformers.Add<string>(s => s.Trim());            // global
cfg.CreateMap<A, B>().AddTransform<string>(s => s.ToUpper()); // per map
```

## IQueryable projection (Entity Framework Core)

```csharp
List<OrderDto> dtos = await db.Orders
    .Where(o => o.Status == OrderStatus.Shipped)
    .ProjectTo<OrderDto>(mapper.ConfigurationProvider)
    .ToListAsync();
```

`ProjectTo` builds a `Select` expression from the maps, so only the needed columns are queried. Only **expression-based** configuration can be translated: conventions, flattening, `MapFrom(s => ...)`, `ConstructUsing(s => new ...)` and `ConvertUsing(s => ...)`. Members that use delegates, resolvers or value converters raise a clear error; `Ignore()` them or rewrite them as expressions. You can inspect the generated expression with `config.BuildProjection<Order, OrderDto>()`.

### Query parameters

Variables captured in `MapFrom` expressions become query parameters, so one map can depend on per-request values:

```csharp
string currentUser = "";   // captured in the map, replaced per query
cfg.CreateMap<Post, PostDto>()
   .ForMember(d => d.IsMine, o => o.MapFrom(s => s.Author == currentUser));

var posts = db.Posts.ProjectTo<PostDto>(config, new { currentUser = User.Identity!.Name });
// or: ProjectTo<PostDto>(config, new Dictionary<string, object> { ["currentUser"] = name })
```

EF Core sees the value as a SQL parameter, so the query plan is reused across users.

### Explicit expansion

Large or expensive members can be left out of projections unless the caller asks for them:

```csharp
cfg.CreateMap<Author, AuthorDto>()
   .ForMember(d => d.Books, o => o.ExplicitExpansion());

db.Authors.ProjectTo<AuthorDto>(config);                    // no Books column or join
db.Authors.ProjectTo<AuthorDto>(config, null, a => a.Books); // Books included
db.Authors.ProjectTo<AuthorDto>(config, null, "Books.Reviews"); // nested paths as strings
```

`mapper.Map()` always maps explicitly expanded members; the option only affects `ProjectTo`.

## Inspecting a configuration

```csharp
TypeMap map = config.FindTypeMapFor<Order, OrderDto>();
foreach (var pm in map.PropertyMaps)
    Console.WriteLine($"{pm.DestinationName} <- {string.Join(".", pm.SourceMembers.Select(m => m.Name))}");
string[] unmapped = map.GetUnmappedPropertyNames();
```

## Errors

* `MapperConfigurationException`: invalid configuration (duplicate maps, unmapped members, bad `Include`, unknown source path). `Errors` lists every problem.
* `MappingException`: a mapping failed at runtime. The message names the source/destination types and the member, and `InnerException` holds the original error.

## Not included

Expression mapping (translating expressions written against DTOs), `IDataReader` mapping and runtime-generated proxies for interface destination types are not part of ConventionMapper; map to concrete classes instead.

## Trimming and Native AOT

The runtime `ConventionMapper` package uses reflection and runtime code generation, so it is not compatible with trimming or Native AOT; for those apps use [`ConventionMapper.SourceGenerator`](#build-time-mapping-source-generator), which generates plain C# with no reflection. The configuration constructors and `AddConventionMapper` are marked `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so trimmed and AOT apps get a clear build warning instead of failing at runtime.

## Samples

[`samples/ConventionMapper.Sample.WebApi`](https://github.com/katragvb/Conventionmapper/tree/main/samples/ConventionMapper.Sample.WebApi) is an ASP.NET Core minimal API on EF Core (in-memory) that shows `ProjectTo` with a query parameter and explicit expansion, `mapper.Patch`, collection merging by key and `Explain()` in one small app:

```
dotnet run --project samples/ConventionMapper.Sample.WebApi
```

[`samples/ConventionMapper.Sample.Aot`](https://github.com/katragvb/Conventionmapper/tree/main/samples/ConventionMapper.Sample.Aot) is a Native AOT console app that uses only build-time mapping:

```
dotnet publish samples/ConventionMapper.Sample.Aot -c Release -r linux-x64 -o out && ./out/ConventionMapper.Sample.Aot
```

## Building and publishing

```
./build.sh 1.6.0      # or ./build.ps1 -Version 1.6.0
```

This restores, builds, runs the tests and writes `ConventionMapper.1.6.0.nupkg` (+ `.snupkg`) and `ConventionMapper.SourceGenerator.1.6.0.nupkg` to `./artifacts`. Pushing a `v1.6.0` tag publishes both packages to nuget.org through trusted publishing (see `.github/workflows/publish.yml`).

## Contributing

Issues and pull requests are welcome; see [CONTRIBUTING.md](https://github.com/katragvb/Conventionmapper/blob/main/CONTRIBUTING.md). Report security problems privately as described in [SECURITY.md](https://github.com/katragvb/Conventionmapper/blob/main/SECURITY.md).

## Changelog

See [CHANGELOG.md](https://github.com/katragvb/Conventionmapper/blob/main/CHANGELOG.md).

## License

MIT
