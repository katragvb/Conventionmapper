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

The expected pairs must come from you (or from a spec), not from the code under test. With the source generator you can
pass the generated lineage instead, `MapperAssert.LineageOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Claim))`,
but that records the mapping configuration, so a configuration mistake such as a `[MapMember]` that swaps two members is
in the lineage too and passes. It only proves the generated code does what the configuration says. Pair it with the
snapshot-tested data-flow report, where a reviewer sees the swap, or state the pairs that matter explicitly.

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
members it copies from a filled source, and each value must come from the member the generator recorded. A new map gets
this coverage before anyone writes a dedicated test.

**What it can't catch:** it compares the generated code with the generator's own record of the configuration, so a
mistake in the configuration itself passes. If `[MapMember("CompleteUser.Id", "SubmitUserId")]` swaps two members, the
code and the record agree, and the check passes. It finds problems the configuration doesn't show: exceptions on real
values (a parse, a `[MapRequired]` member, a conversion method), and generator bugs. To check the configuration is
*right*, state the expected sources yourself with `WritesFrom` (above) or the expected members with `WritesExactly`.

When sample values aren't valid input (a string that is parsed, say), pass options:
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

## Snapshots of the generated code

A library upgrade or an attribute change can alter what the generator writes for a mapper. Snapshot one method's generated
code in its own file, so the change fails a test that names that mapper:

```csharp
[Fact]
public void ToDto_generated_code_is_reviewed() =>
    MapperAssert.GeneratedCodeMatches(typeof(Mappings), nameof(Mappings.ToDto), new[] { typeof(Order) });
    // generated-code/Mappings.ToDto(Order).cs, next to this test file

[Fact]
public void Generated_code_is_reviewed() =>
    MapperAssert.GeneratedCodeMatchesDirectory(typeof(Mappings));   // generated-code/Mappings/<method>.cs, one per method
```

The project that contains the mapper needs `<ConventionMapperEmbedGeneratedCode>true</ConventionMapperEmbedGeneratedCode>`.
A missing file is created and the test fails once so it gets reviewed and committed; a change fails with a line diff; run the
tests with `CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1` to accept changes. In directory mode, a file for a method that no longer exists
fails as stale.

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

### Builders

`SampleData.Builder<T>()` starts from a filled object and sets only what a test cares about, so setup stays short:

```csharp
var referral = SampleData.Builder<Referral>()
    .With(r => r.Status, "A")
    .With(r => r.Address.City, "Leeds")      // nested; missing objects on the way are created
    .Without(r => r.ClosedOn)                // null / default
    .Build();

var referrals = SampleData.Builder<Referral>().With(r => r.Number, i => 1000 + i).BuildMany(3);   // distinct values
var blank = SampleData.Builder<Referral>().Blank().With(r => r.Id, 7).Build();                     // start empty
```

Private and init-only setters and get-only auto-properties can be set, so entities that guard their state can be built too.
`Configure(r => ...)` runs any other setup, and builders take the same `SampleDataOptions`.

`Randomized(seed)` switches to random edge cases, repeatable with the seed. Random values no longer carry member names,
so `WritesFrom` and `AllMappersMatchWriteSets` refuse it; it is meant for `RoundTrips` and your own tests.
