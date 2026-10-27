# Contributing to ConventionMapper

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with the bug template. The fastest fix comes from a small, complete example: the source and destination classes, the configuration (or `[GeneratedMapper]` class), what you expected and what happened. Include the package version and target framework.

## Suggesting a feature

Open an issue with the feature template and describe the problem first, then the API you have in mind. Small examples of how you would call it help a lot.

Check [ROADMAP.md](ROADMAP.md) for planned work, and [docs/adr](docs/adr/README.md) for the design rules (guesses are errors, caller-owned values are parameters, explicit mode and the others). A proposal that changes one of those rules should say which, and why; accepted changes get a new decision record.

A new diagnostic needs a section in [docs/diagnostics.md](docs/diagnostics.md) with the cause, an example and the fix; a test checks that every code has one.

## Pull requests

1. Open an issue first for anything bigger than a small fix, so the approach can be agreed before you write code.
2. Fork the repository and create a branch from `main`.
3. Build and test:

   ```
   ./build.sh            # or ./build.ps1 on Windows
   ```

   This restores, builds, runs all tests and packs both packages.
4. Add tests for your change. The test project covers the runtime mapper (`MapperTests`, `FeatureTests`, ...), the source generator (`GeneratedMapperTests` for generated code, `RoslynDiagnosticsTests` for diagnostics) and EF Core projections.
5. Keep the public API compatible: the build validates the package against the last published version and fails on breaking changes.
6. Add a line to `CHANGELOG.md` under the unreleased version.

## Project layout

| Folder | Contents |
| --- | --- |
| `src/ConventionMapper` | Runtime mapper (NuGet package `ConventionMapper`) |
| `src/ConventionMapper.SourceGenerator` | Build-time mapper (NuGet package `ConventionMapper.SourceGenerator`) |
| `src/ConventionMapper.Analyzers`, `src/ConventionMapper.CodeFixes` | Analyzer and IDE fixes shipped inside `ConventionMapper` |
| `src/Shared` | Member matching shared by the generator and the analyzer |
| `tests` | xUnit tests (also run on .NET Framework 4.8 on Windows) |
| `benchmarks` | BenchmarkDotNet suite; push to a `bench/*` branch to run it in CI |
| `samples` | ASP.NET Core + EF Core sample and a Native AOT sample |

## License

By contributing you agree that your contributions are licensed under the MIT License.
