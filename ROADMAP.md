# Roadmap

ConventionMapper has one maintainer, so this is a statement of direction, not a schedule. Design rules are recorded in
[docs/adr](docs/adr/README.md); proposals that change one should say which.

## Released

See the [changelog](CHANGELOG.md). 1.9.0 added values (constants, computed and conditional members, required members, column
lengths), EF Core awareness, new kinds of methods (patch, combine, merge by key, database rows, clone, equality, validation,
diffs, redaction), Protobuf and immutable collections, data protection checks beyond mappers, migration tooling for hand-written
copies, and behavior tests in ConventionMapper.Testing.

## Next

- **Command-line tool** (`dotnet conventionmapper`): `scan` lists migration candidates per project (the CM0029 analysis without
  an IDE), and `diff --to <version>` shows how the generated code would change before a package upgrade.
- **Pull request summary**: a GitHub Action that turns `DataFlowReport.Json` into a plain-English comment on the pull request
  ("ToEndorsementLocation now also writes TaxCode").
- **Benchmarks** against hand-written code and other mappers, published in the README with the BenchmarkDotNet setup.
- **Online playground**: a Blazor WebAssembly page that runs the generator in the browser and shows the generated code,
  diagnostics and report, for evaluation and bug reports.

## Later

- An EF Core model export at build time (for configuration the source reading can't see: conventions plugins, models built in
  loops).
- OpenLineage output for the column-level lineage.
- Opt-in telemetry (`[MapTelemetry]`): counts of null substitutions, defaulted values and parse failures through
  `System.Diagnostics.Metrics`.

## Not planned

- Native AOT support in the runtime package. It is reflection-based by design; trimmed and AOT apps use the source generator.
- Silent conventions (see [0001](docs/adr/0001-guesses-are-errors.md)).
