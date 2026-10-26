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

## Sample data

`SampleData.Create<T>()` builds the filled objects used above. Strings carry the member name (`"Street1-7"`), so a value
that lands in the wrong member is easy to spot in a failure.
