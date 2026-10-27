# Diagnostics

Every ConventionMapper diagnostic, with the cause, an example and the fix. The IDE's error list links here: click the code.
CM0001 to CM0033 come from `ConventionMapper.SourceGenerator`, CM0100 from its build targets, and CM1001 to CM1003 from the
analyzers in the runtime `ConventionMapper` package. Change a severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.CM0003.severity = warning
```

In the examples, `Order` is the source and `OrderDto` the destination.

## CM0001

**Unmapped destination member** · Error

A destination member has no source member with a matching name, `Get` method or flattened path, so the generated code would
leave it at its default without anyone deciding that.

```csharp
public class OrderDto { public int Id { get; set; } public string Buyer { get; set; } = ""; }
public static partial OrderDto ToDto(Order order);   // CM0001: 'Buyer' has no matching source member
```

**Fix:** map it from another member with `[MapMember("Customer.Name", nameof(OrderDto.Buyer))]`, pass it as a parameter
(`ToDto(Order order, string buyer)`), or leave it out with `[MapIgnore(nameof(OrderDto.Buyer))]`. To copy only members you
list, use `[MapExplicit(...)]`. The IDE fixes add the attribute for you.

## CM0002

**Member types cannot be mapped** · Error

The source member exists, but there is no conversion from its type to the destination member's type.

```csharp
public class Order { public Money Total { get; set; } }
public class OrderDto { public decimal Total { get; set; } }   // CM0002: Money → decimal
```

**Fix:** add a conversion method to the mapper class (`[MapConversion] private static decimal ToAmount(Money m) => m.Amount;`),
declare a mapping method for the pair, map another member with `[MapMember("Total.Amount", nameof(OrderDto.Total))]`, or
`[MapIgnore]` it.

## CM0003

**Nullable source mapped to a non-nullable destination** · Info

`int?` → `int` (or a nullable path such as `Customer.Address.City`): when the source is null, the destination gets the default
value (`0`, `null`).

**Fix:** usually nothing. To use a specific value, add `[MapNullSubstitute("Count", 1)]`. To fail instead, add
`[MapRequired("Count")]`. Raise the severity to warning to review each case.

## CM0004

**No usable constructor** · Error

The destination has no accessible constructor whose parameters can all be mapped: a record or immutable class needs a value the
source doesn't have.

```csharp
public record OrderDto(int Id, string Buyer);   // no source member for 'buyer'
```

**Fix:** add a matching source member, map the parameter with `[MapMember("Customer.Name", "buyer")]`, pass it as a method
parameter, or add a constructor the source can fill.

## CM0005

**Mapper must be partial** · Error

The `[GeneratedMapper]` class, and every class it is nested in, must be `partial` so the generator can add the implementation.

**Fix:** add `partial` to each class in the chain.

## CM0006

**Unsupported mapping method** · Error

The method's shape is not one the generator implements. Supported: `TDest M(TSource source, extra...)` (create),
`void M(TSource source, TTarget target, extra...)` (update), projections (`IQueryable<TDest> M(this IQueryable<TSource>)`,
`Expression<Func<TSource, TDest>> M()`), and the special methods marked with an attribute (`[MapRedact]`, `[MapLogString]`,
`[MapClone]`, `[MapEquality]`, `[MapValidate]`, `[MapCombine]`, `[MapMergeKey]`, diffs and `IDataRecord` readers). Generic
methods are not supported.

**Fix:** change the signature to one of the supported shapes, or write the method by hand outside the partial declaration.

## CM0007

**Unmapped enum value** · Warning

Enums map by name. A source value without a destination value of the same name makes the generated `switch` throw
`ArgumentOutOfRangeException` at runtime.

```csharp
enum Status { Draft, Open, Archived }   enum StatusDto { Draft, Open }   // 'Archived' has no match
```

**Fix:** add the value to the destination enum, or map the member with a conversion method that handles it.

## CM0008

**Unknown member in mapping attribute** · Warning

A name in `[MapMember]`, `[MapIgnore]`, `[MapExplicit]`, `[MapNullSubstitute]`, `[MapAllowParse]`, `[MapReviewed]` or another
mapping attribute doesn't match any member, usually after a rename.

**Fix:** correct the name. Use `nameof(OrderDto.Name)` so renames update it (the CM0020 fix converts strings).

## CM0009

**Sensitive member would be copied** · Error

A `[Sensitive]` source member would be copied into a destination member that isn't `[Sensitive]`, so it could reach an API
response or a log.

```csharp
public class User { [Sensitive] public string PasswordHash { get; set; } = ""; }
public class UserDto { public string PasswordHash { get; set; } = ""; }   // CM0009
```

**Fix:** `[MapIgnore(nameof(UserDto.PasswordHash))]`, or mark the destination `[Sensitive]` too when it is protected (a cache
entry, say).

## CM0010

**Recursive type in a projection** · Warning

A projection reaches a type that contains itself (a tree). An expression can't expand it, so the member is left null.

**Fix:** `[MapIgnore]` the member and load it in a separate query, or project to a DTO without the recursive member.

## CM0011

**Mapping method parameter is not used** · Error

An extra parameter of the mapping method matches no destination member, so the caller's value would be ignored.

**Fix:** rename it to the member it sets (`createdAt` sets `CreatedAt`), use it in a `[MapMember("createdAt", "Created")]` path,
or remove it.

## CM0012

**Null substitute has no effect** · Warning

`[MapNullSubstitute]` on a member whose source can never be null.

**Fix:** remove the attribute.

## CM0013

**Invalid null substitute** · Error

The substitute isn't a constant that converts implicitly to the member type (`[MapNullSubstitute("Count", "none")]` on an
`int`).

**Fix:** use a constant of the member's type.

## CM0014

**Ambiguous [MapMember] source** · Error

A `[MapMember]` path matches both a source member and a parameter of the method, and neither in its exact case.

**Fix:** write the name in its exact case: `"CreateUser"` for the source member, `"createUser"` for the parameter, or start the
path with the source parameter's name (`"source.CreateUser"`).

## CM0015

**Ambiguous source for a destination member** · Error

The conventions found more than one possible source: a property and a `Get` method (`Total` / `GetTotal()`), names differing
only in case, two flattenings, or a parameter and a flattened path. The generator never picks one silently.

**Fix:** choose with `[MapMember("BillingAddress.City", nameof(OrderDto.BillingAddressCity))]`.

## CM0016

**Conversion can fail or lose data at runtime** · Warning

`int.Parse`, `Enum.Parse`, a narrowing cast (`long` → `int`, `decimal` → `double`) or another conversion that can throw or lose
data.

**Fix:** if it is intended, allow it for that member with `[MapAllowParse("PostalCode")]`. To fail loudly instead of losing
data, add `[MapChecked("Count")]`, which throws `OverflowException` naming the member.

## CM0017

**Static method used as a conversion** · Warning

A one-argument static method of the mapper class was used as a conversion for some members, but isn't marked `[MapConversion]`,
so it may be a helper that was never meant to convert every member of those types.

**Fix:** mark it `[MapConversion]` if that is intended, or move it out of the mapper class. With
`[GeneratedMapper(RequireMapConversion = true)]` only marked methods are used.

## CM0018

**Invalid [MapConversion] method** · Error

A method marked `[MapConversion]` can't convert: it isn't static, is generic, or doesn't take exactly one value.

**Fix:** make it `static T To(S value)` in the mapper class.

## CM0019

**Invalid [MapInherit]** · Error

`[MapInherit]` names an unknown method, one with other source or destination types, an ambiguous overload, or forms a cycle.

**Fix:** name the method with `nameof`, and when it is overloaded add its parameter types:
`[MapInherit(nameof(ToDto), typeof(Order))]`.

## CM0020

**Member name can use nameof** · Hidden

A member name written as a string in a mapping attribute.

**Fix:** the IDE fix turns `"Name"` into `nameof(OrderDto.Name)`; fix all in a document, project or solution.

## CM0021

**Projection may not translate to SQL** · Warning

Part of a projection runs on the client or formats differently in the database: a conversion method (only allowed in the final
`Select`), or `ToString()` on dates, numbers, booleans or enums.

**Fix:** map that member after the query, add a computed column, `[MapIgnore]` it, or set `[GeneratedMapper(Provider = "...")]`
so the check matches your database.

## CM0022

**Unknown projection provider** · Warning

`[GeneratedMapper(Provider = "...")]` names a provider the generator doesn't know.

**Fix:** use `SqlServer`, `PostgreSql`, `Sqlite`, `Cosmos` or `InMemory`.

## CM0023

**Members assigned after a generated mapping** · Info

```csharp
var dto = order.ToDto();
dto.Note = note;          // CM0023
```

The object is finished in two places, and a forgotten assignment is a silent default.

**Fix:** the IDE fix adds an overload `[MapInherit(nameof(ToDto))] OrderDto ToDto(this Order order, string note)` and passes the
values to it.

## CM0024

**Out-of-date source generator is running** · Warning

After a package upgrade the IDE still runs the previous generator, so the generated code is stale and can cause confusing errors
such as ambiguous calls.

**Fix:** restart Visual Studio or Rider, or run *.NET: Restart Language Server* in VS Code. Command-line builds always use the
new version.

## CM0025

**Invalid member value attribute** · Error

A `[MapConstant]`, `[MapFrom]`, `[MapRequired]`, `[MapTruncate]`, `[MapMergeKey]`, `[MapWhen]`, `[MapFormat]`, `[MapRedact]`,
`[MapLogString]` or similar attribute doesn't fit its member or method: a constant of the wrong type, an unknown or wrongly shaped
method, `[MapRequired]` in a projection or together with `[MapNullSubstitute]`, `[MapTruncate]` without a known length. The
message says which.

**Fix:** as the message says, for example `[MapTruncate("City", 50)]` when the member has no `[MaxLength]`.

## CM0026

**Member looks like personal data** · Info

A copied member whose name looks personal (`Ssn`, `TaxId`, `DateOfBirth`, `Email`, `Phone`, `BankAccount`, `Iban`, `Passport`,
`Password`, `ApiKey`, also with prefixes such as `StrSsn`) isn't `[Sensitive]`.

**Fix:** the IDE fix *Mark [Sensitive]* adds the attribute. If the data isn't personal, ignore it, or raise the severity to
warning to require a decision for each one.

## CM0027

**String can be longer than the destination allows** · Warning (EF Core entities) / Info

The destination has a maximum length (`[MaxLength]`, `[StringLength]` or `HasMaxLength`) and the source can be longer. For an
entity the insert would fail or cut the value.

**Fix:** `[MapTruncate("City")]` to cut it to fit, or `[MapReviewed("City")]` when the source is known to be short enough.

## CM0028

**Mapping writes a database-generated value or a navigation** · Warning

A map writes a value the database generates, or a navigation property, into an EF Core entity:

- a **generated key**: an identity, or a key with a database default such as `newid()` or `NEXT VALUE FOR` a sequence.
  Copying it between rows causes duplicate keys.
- a **generated column**: computed (`HasComputedColumnSql`) or `ValueGeneratedOnAdd` / `OnUpdate`. The write fails or is
  ignored.
- a **navigation**: EF Core attaches the source's related rows to the new row.

A column that only has a database default (`HasDefaultValueSql("('U')")`) is not reported: the database uses the default
only when an insert leaves the column out, so copying a value into it is normal.

```csharp
[MapExplicit(nameof(EndorsementLocation.Id), nameof(EndorsementLocation.City))]   // CM0028: Id is generated
public static partial EndorsementLocation Copy(QuoteLocation source);
```

**Fix:** leave the member out, or acknowledge it with `[MapReviewed("Id")]` when it is deliberate. See
[Copying one entity to another](source-generator.md#copying-one-entity-to-another).

## CM0029

**Hand-written copy can be a generated mapping** · Info

An object initializer or conversion operator copies at least three same-name members from one object by hand.

**Fix:** *Convert to generated mapping*, or for operators *Replace the operator with a generated mapping (update call sites)*.
The threshold is `conventionmapper.copy_min_members` in `.editorconfig`. See
[Migrating hand-written copies](source-generator.md#migrating-hand-written-copies).

## CM0030

**Conversion depends on the server's regional settings** · Warning

A conversion method or `[MapFrom]` method parses or formats text in the current culture (`decimal.Parse(s)`,
`date.ToString("d")`), so the result differs between a developer machine and a server with other regional settings. The
generator's own conversions always use the invariant culture.

**Fix:** pass `CultureInfo.InvariantCulture`, or use `[MapFormat("EffectiveDate", "yyyy-MM-dd")]` on the member.

## CM0031

**Mapping uses an obsolete member** · Warning

A map reads or writes a member marked `[Obsolete]`. Deprecating a column lists every map that still uses it.

**Fix:** map from or to the replacement member, or `[MapIgnore]` it.

## CM0032

**[Sensitive] data written to a log, exception or ToString** · Warning

An object with `[Sensitive]` members (directly, in nested objects or in collections) is passed to an `ILogger` or Serilog call or
a `[LoggerMessage]` method, put into an exception message, or printed by a `ToString()` override that reads a `[Sensitive]`
member, or by a record's compiler-generated `ToString()`. Logging is the most common leak path.

```csharp
log.LogInformation("Saved {User}", user);           // CM0032: User.Ssn is [Sensitive]
log.LogInformation("Saved {User}", Mappings.ToLogString(user));   // fine
```

**Fix:** log a `[MapLogString]` or `[MapRedact]` result, leave the object out, or override `PrintMembers` (records) or
`ToString()` to skip the member.

## CM0033

**Destination member can take the source member's documentation** · Hidden

An undocumented destination member is copied from a member with a `<summary>`.

**Fix:** the IDE fix *Copy documentation from 'Order.Status'* adds the summary and a `Copied from` remark, so API docs explain
where each field comes from.

## CM0100

**Generated code is out of date** · Error (CI builds with `ConventionMapperEmitGeneratedCode`)

The generated code committed to the repository differs from what the build generates, or a file belongs to no mapper.

**Fix:** build locally (it rewrites the folder) and commit the changes. See
[Generated code in pull requests](source-generator.md#generated-code-in-pull-requests).

## CM1001

**Unmapped member in CreateMap** · Warning (runtime package)

`CreateMap<TSource, TDestination>();` where a destination member has no matching source member;
`AssertConfigurationIsValid()` would fail at runtime.

**Fix:** the IDE fix adds `.ForMember(d => d.X, o => o.Ignore())`, for one member or all of them. See
[analyzers.md](analyzers.md).

## CM1002

**Map call can use the typed fast path** · Info (runtime package)

`mapper.Map<TDestination>(source)` where the source type is known at compile time.

**Fix:** the IDE fix rewrites it to `mapper.Map<TSource, TDestination>(source)`.

## CM1003

**Sensitive member copied by CreateMap** · Error (runtime package)

`CreateMap<TSource, TDestination>();` would copy a `[Sensitive]` member into one that isn't. The configuration would also fail
at startup.

**Fix:** the IDE fix ignores the member; or mark the destination `[Sensitive]`.
