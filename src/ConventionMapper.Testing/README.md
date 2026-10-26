# ConventionMapper.Testing

Test helpers for mappings written with [ConventionMapper](https://www.nuget.org/packages/ConventionMapper) or
[ConventionMapper.SourceGenerator](https://www.nuget.org/packages/ConventionMapper.SourceGenerator). No dependency on a
test framework: failures throw `MapperAssertException`, which xUnit, NUnit and MSTest all report.

```
dotnet add package ConventionMapper.Testing
```

## Which members does a map write?

`MapperAssert.WritesExactly` fills a source object (distinct values, nested objects, one-element collections), runs the
map and compares the destination members that got a value with the list you expect:

```csharp
MapperAssert.WritesExactly((LocationData s) => s.ToImportData("GL1", 10m),
    "LocationNumber", "Street1", "City", "GlClassCode", "Exposure");
```

With the source generator, each mapper class has `WriteSets`: the members each generated method writes. Compare the
real behavior with it, without listing members by hand:

```csharp
MapperAssert.WritesExactly((LocationData s) => s.ToImportData(), Mappings.WriteSets["ToImportData(LocationData)"]);
```

Keys are `"Name(ParameterTypes)"` as in the data-flow report, plus `"Name"` for methods without overloads. To survive
renames and signature changes, look the write set up by name and parameter types instead:
`Mappings.WriteSetOf(nameof(Mappings.ToImportData), typeof(LocationData))`, or
`MapperAssert.WriteSetOf(typeof(Mappings), nameof(Mappings.ToImportData), typeof(LocationData))`. Update
methods use `MapperAssert.UpdatesExactly((Dto s, Entity t) => s.Update(t), expected)`.

## Do the values come from the right members?

`WritesExactly` checks which members are written, not where their values come from: a swapped pair (`SubmitUserId`
filled from `CompleteUser.Id`, `CompleteUserId` from `SubmitUser.Id`) passes it. `MapperAssert.WritesFrom` checks each
destination member against the source member path it should be copied from. Every sample value is distinct, so a value
that comes from the wrong member is found, and the failure names the member it really came from:

```csharp
MapperAssert.WritesFrom((Claim c) => c.ToDto(), ("SubmitUserId", "SubmitUser.Id"), ("CompleteUserId", "CompleteUser.Id"));
// CompleteUserId: expected the value of CompleteUser.Id (12), but it holds 9, the value of SubmitUser.Id
```

With the source generator, use the generated lineage, so the data-flow report becomes an executable test:

```csharp
MapperAssert.WritesFrom((Claim c) => c.ToDto(), MapperAssert.LineageOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Claim)));
```

Nested objects mapped by another method are left to that method's own check. Built-in conversions (numbers, `ToString`,
enum names) are followed; a conversion it can't follow is only reported when the value clearly came from elsewhere.

## A baseline for every map

```csharp
[Fact]
public void Every_map_matches_its_write_set() =>
    MapperAssert.AllMappersMatchWriteSets(typeof(Mappings));
```

Runs every generated create and update method with sample data (extra parameters filled too) and compares it with the
generator's `WriteSets` and `Lineage`: it must not throw, must not write members outside its write set, must write the
members it copies from a filled source, and must copy each from the right member. A new map gets this coverage before
anyone writes a dedicated test. When sample values aren't valid input (a string that is parsed, say), pass options:
`AllMappersMatchWriteSets(typeof(Mappings), new SampleDataOptions().For<Row, string>(r => r.Code, n => n.ToString()))`.
Projections and collection merges are skipped.

## Round trips

For a map and its reverse, `back(to(x))` must equal `x`, deeply (nested objects member by member, collections item by item):

```csharp
MapperAssert.RoundTrips<Person, PersonDto>(Mappings.ToDto, Mappings.ToPerson);

// 100 randomized inputs: nullable members sometimes null, empty, blank, very long and non-ASCII strings,
// zero and extreme numbers, empty and multi-item collections.
MapperAssert.RoundTrips<Person, PersonDto>(Mappings.ToDto, Mappings.ToPerson, runs: 100);

// Members the reverse map can't restore on purpose:
MapperAssert.RoundTrips<Person, PersonLite>(Mappings.ToLite, Mappings.FromLite, ignore: new[] { "Nickname" });
```

A failure lists the member paths that changed, or the exception, with the seed of that run. Replay it with
`options: new SampleDataOptions().Randomized(seed)`.

## Is every mapping method tested?

```csharp
[Fact]
public void Every_mapping_method_has_a_test() =>
    MapperAssert.AllMethodsCovered(typeof(Mappings), typeof(MappingTests).Assembly);
```

Fails with the list of generated mapping methods that no code in the test assembly calls (directly, as a method group or
inside an expression tree).

A call alone doesn't check anything, so there is a stricter version: every create and update method must run inside a
`WritesExactly` or `UpdatesExactly` check (projections must at least be called).

```csharp
[Fact]
public void Every_mapping_method_is_verified() =>
    MapperAssert.AllMethodsVerified(typeof(Mappings), typeof(MappingTests).Assembly);
```

## Is every mapping method used?

```csharp
[Fact]
public void No_unused_maps() =>
    MapperAssert.AllMethodsUsed(typeof(Mappings), typeof(OrderService).Assembly);
```

Fails for mapping methods that no code in the given production assemblies uses: maps only tests call, or nothing does,
are candidates for deletion. A method reached through another used method of the mapper (a nested object's map) counts
as used.

## Sample data

`SampleData.Create<T>()` builds the filled objects used above. Strings carry the member name (`"Street1-7"`), so a value
that lands in the wrong member is easy to spot in a failure. Constructor parameters are filled, and a private
parameterless constructor (common on EF Core entities) is used when there is no public one.

`SampleDataOptions` customizes it; every check that fills objects takes them:

```csharp
var options = new SampleDataOptions()
    .For<Address, string>(a => a.ZipCode, n => (10000 + n).ToString())   // valid values for one member
    .For<Money>(() => new Money(10m, "EUR"))                            // every value of a type
    .ForMember("CountryCode", () => "GB")                               // a member of this name in any type
    .Enum(Status.Active, Status.Closed)                                 // only these enum values
    .Construct<IShape>(() => new Square());                             // interfaces, or types without a usable constructor
options.CollectionSize = 3;           // items per collection (default 1)
options.MaxDepth = 2;                 // nesting depth (default 4)
options.FillNonPublicSetters = true;  // also private and init-only setters

var address = SampleData.Create<Address>(options);
```

`Randomized(seed)` switches to random edge cases, repeatable with the seed. Random values no longer carry member names,
so `WritesFrom` and `AllMappersMatchWriteSets` refuse it; it is meant for `RoundTrips` and your own tests.
