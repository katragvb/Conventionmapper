# ConventionMapper.Testing.EntityFrameworkCore

Database checks for mappings into EF Core entities. The source generator's warnings about keys, lengths and database
defaults (CM0027, CM0028, CM0034) come from the EF Core model; these checks run against a real database, so they also catch
what the model gets wrong. Failures throw `MapperAssertException` from
[ConventionMapper.Testing](https://www.nuget.org/packages/ConventionMapper.Testing), which xUnit, NUnit and MSTest all report.

```
dotnet add package ConventionMapper.Testing.EntityFrameworkCore
```

Supports SQL Server and SQLite, EF Core 8 and later.

## Does what the map wrote survive the database?

`DatabaseAssert.SavesRoundTrip` maps a source object, inserts the entity, reads it back and fails for every column whose
stored value differs from what the map wrote. It runs inside a transaction that is rolled back (or inside the test's own
transaction when one is open), so nothing stays in the database.

```csharp
[Test]
public void ToEndorsementForm_keeps_every_value()
{
    var quoteForm = new QuoteForm { FormName = "IL 00 21", PrintText = false };

    DatabaseAssert.SavesRoundTrip(context, (QuoteForm f) => f.ToEndorsementForm(policyId: 7), quoteForm);
}
// Form.PrintText: the map wrote false, the database holds true (the column has a database default: EF Core leaves a
// property holding its CLR default out of the insert, so the default applied; see CM0034).
```

What it finds:

- **Database defaults that replace a copied value.** EF Core leaves a non-nullable `bool` or number holding its CLR default
  (`false`, `0`) out of the insert when the column has a default, so the database's default is stored instead.
- **Text the database cut, numbers it rounded** (decimal scale, `real`), and conversions that don't round-trip.
- **Inserts that fail**: a value too long for its column, a missing foreign key row, a `NOT NULL` column the map leaves
  null. The failure carries the database's message.

Without a source, `SampleData.Create<TSource>()` fills one. `prepare` sets what the map leaves to the caller (foreign keys of
rows the test seeded), and `ignore` skips members the database is expected to change. Generated keys, computed columns and
row versions are not compared. `datetime` columns may differ by up to 1/300 of a second.

```csharp
DatabaseAssert.SavesRoundTrip(context, (QuoteForm f) => f.ToEndorsementForm(policyId: 0), quoteForm,
    prepare: form => form.PolicyId = seededPolicy.PolicyId,
    ignore: new[] { nameof(EndorsementForm.ModifiedDate) });
```

## Every map at once

`DatabaseAssert.AllMappersSaveRoundTrip` runs `SavesRoundTrip` for every generated create method of a mapper class whose result
is an entity of the context, so a new map is checked against the database before it has a test of its own:

```csharp
[Test]
public void Every_map_into_the_database_round_trips() =>
    DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(Mappings));
// 1 of 24 runs of 12 maps into the database don't round-trip:
//   ToEndorsementForm(QuoteForm, Int32) with false and 0 values: After saving and reading back, these values changed:
//     EndorsementForm.PrintText: the map wrote false, the database holds true (... see CM0034).
```

Each map runs twice: with sample data, and with `false` and `0` for every bool and number (sample data never holds them, and
they are what a database default replaces). Parameters get sample data too. Foreign keys are not checked inside the rolled-back
transaction, so no related rows need seeding; sample strings are cut to the column's maximum length (that's CM0027's job),
sample decimals are rounded to the column's scale, and xml columns get a valid document. Inside the test's own transaction,
each run is rolled back to a savepoint, so runs with the same sample keys don't collide. Pass `SampleDataOptions` for values the database constrains (codes a check constraint
allows) and `skip` for methods to leave out.

## Does the model match the database?

Scaffolded models drift: a column is widened, a default is added, a column becomes nullable, and nobody scaffolds again.
`DatabaseAssert.ModelMatchesDatabase` compares every mapped table and column of the model with the schema: whether it exists,
its nullability, its maximum length (SQL Server) and whether it has a default.

```csharp
[Test]
public void The_model_matches_the_database() =>
    DatabaseAssert.ModelMatchesDatabase(context);                       // or (context, typeof(Policy), typeof(Form))
// The EF Core model does not match the database:
//   dbo.tblLocations.TaxCode (Location.TaxCode): maximum length unlimited in the model, 5 in the database.
//   dbo.tblForms.PrintText (Form.PrintText): the database column has the default ((1)), the model does not know it.
```

Run it against a database with the real schema, such as a copy of production. A database created from the model
(`EnsureCreated`, migrations of the same model) always matches.

### Without a database in CI

Export the real schema once and commit it; every test run then checks the model against the file:

```csharp
// Once, against a copy of production (and again after schema changes):
DatabaseAssert.ExportSchema(productionCopyContext, "schema/premium.json");

// In the test project (copy the file to the output directory):
[Test]
public void The_model_matches_the_production_schema() =>
    DatabaseAssert.ModelMatchesSchema(context, "schema/premium.json");
```

The file lists, for the model's tables only, each column's nullability, maximum length (SQL Server) and default, sorted for
readable diffs. The context only needs its model; no connection is opened.
