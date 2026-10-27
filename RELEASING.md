# Releasing

Running the `build-and-publish` workflow manually on `main` (Actions → build-and-publish → Run workflow) publishes
`<version>-preview.<run>` packages to nuget.org, unless `<version>` is already released, and a `v<version>` tag publishes the
release. Previews are published on request rather than on every push because nuget.org keeps every version: they can be
unlisted, not deleted. Both 1.9.0 problems (a false positive on scaffolded EF Core models, and a fix that
existed only in a local copy) would have been caught by the steps below.

## Before tagging

1. **Everything is on `main`.** The fix or feature is merged through a pull request; nothing exists only in a local clone or
   an unmerged branch. `git log origin/main` shows it.
2. **Versions and notes.** `<Version>` in `src/ConventionMapper`, `src/ConventionMapper.SourceGenerator`,
   `src/ConventionMapper.Testing` and `src/ConventionMapper.Testing.EntityFrameworkCore` is the version being released, and
   `CHANGELOG.md` has its section (dated) with an **Upgrading** list of new warnings and behavior changes.
3. **CI is green on `main`**, including the Windows (.NET Framework) tests, the SQL Server tests, the build-integration check
   and the AOT sample.
4. **Try the preview in a real codebase.** Run the workflow manually on `main`, then reference its `-preview.<run>` packages in at least one
   consumer (a large project with scaffolded EF Core entities and warnings as errors is the best test), build it, run its
   tests, and read every new diagnostic. A warning that is wrong there is a bug to fix before tagging, not a `[MapReviewed]`.
5. **Re-record that consumer's snapshots** (`CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1`) and read the diff: it shows what the
   release changes in generated code and data flow.

## Tagging

```
git checkout main && git pull
git tag v1.2.3 && git push origin v1.2.3
```

The tag's build runs the tests again, packs with the tag's version, publishes to nuget.org, and then opens a pull request
merging `main` into every `feature/*` branch that doesn't contain it yet, so the next version starts from the release.
nuget.org takes a few minutes to validate and index new packages; mirrors (Nexus, Artifactory) pick them up after that.

## After the release

1. Merge the forward-merge pull requests into the open `feature/*` branches.
2. Move `<PackageValidationBaselineVersion>` in the package projects to the version just released, so the next release is
   checked against it for breaking API changes.
3. Upgrade the consumer from step 4 to the released version and remove `[MapReviewed]` entries that CM0035 reports.
