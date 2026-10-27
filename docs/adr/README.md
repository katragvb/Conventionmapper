# Architecture decision records

Short records of the design rules behind ConventionMapper and why they exist. Read them before proposing a change that touches
one of these rules; if a rule should change, add a new record that supersedes the old one rather than editing it.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-guesses-are-errors.md) | Guesses are build errors | Accepted |
| [0002](0002-caller-owned-values-are-parameters.md) | Values the caller owns are method parameters | Accepted |
| [0003](0003-explicit-mode.md) | Explicit mode for copies that matter | Accepted |
| [0004](0004-exact-case-mapmember.md) | Exact case decides `[MapMember]` sources | Accepted |
| [0005](0005-plain-csharp-no-runtime-dependency.md) | The generator writes plain C# with no runtime dependency | Accepted |
| [0006](0006-one-model-for-code-and-reports.md) | Code, report, write sets and lineage come from one model | Accepted |
| [0007](0007-sensitive-data-flows-only-to-sensitive-members.md) | `[Sensitive]` data flows only into `[Sensitive]` members | Accepted |
| [0008](0008-risk-is-acknowledged-per-member.md) | Risks are acknowledged per member, not switched off globally | Accepted |
| [0009](0009-invariant-culture.md) | Generated conversions use the invariant culture | Accepted |

New records use [the template](template.md).
