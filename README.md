# ConventionMapper

A convention-based object-object mapper for .NET. It removes the boring code that copies one object into another (entities → DTOs, view models, API contracts), with an API that will feel familiar if you have used AutoMapper.

Targets **.NET Standard 2.0** (works on .NET Framework 4.6.1+, .NET Core, Unity, Xamarin) and **.NET 8+**.

```
dotnet add package ConventionMapper
```

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

This registers `MapperConfiguration` / `IConfigurationProvider` (singleton) and `IMapper` (transient). Every `Profile` and every `[AutoMap]` class in the scanned assemblies is added, and these implementations are registered as transient services so they can take constructor dependencies:

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
[AutoMap(typeof(Person), ReverseMap = true)]
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

## Beyond AutoMapper: built-in extras

These need extra packages or workarounds in AutoMapper; here they are part of the core library.

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

## Differences from AutoMapper

ConventionMapper is an independent implementation, not a fork. Collection merging and strict enum mapping (separate AutoMapper packages) are built in. Not included: the ExpressionMapping, EF6 and Data extensions, logging integration and license keys, runtime-generated proxies for interface destination types, and `ProjectTo` parameters / explicit expansion.

## Building and publishing

```
./build.sh 1.0.0      # or ./build.ps1 -Version 1.0.0
```

This restores, builds, runs the tests and writes `ConventionMapper.1.0.0.nupkg` and `.snupkg` to `./artifacts`. To publish, upload the `.nupkg` on nuget.org, or push a `v1.0.0` tag with the `NUGET_API_KEY` secret set (see `.github/workflows/publish.yml`).

## License

MIT
