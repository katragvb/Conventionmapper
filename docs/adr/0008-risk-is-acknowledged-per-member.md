# 0008. Risks are acknowledged per member, not switched off globally

**Status:** Accepted

## Context

Some conversions and writes are legitimate but dangerous: parsing strings, narrowing numbers, writing keys, copying long strings
into short columns. A global "allow" switch hides the next, unintended case along with the reviewed ones.

## Decision

Each risk is a warning that names the member, and each acknowledgment names the member too: `[MapAllowParse("PostalCode")]`,
`[MapReviewed("PolicyId")]`, `[MapChecked("Count")]`, `[MapTruncate("City")]`. Acknowledgments may sit on a method or the mapper
class, never project-wide. Severities can still be changed in `.editorconfig` for teams that want stricter defaults.

## Consequences

A new risky member in a reviewed map is still reported. The report shows reviewed items as such (`key (reviewed)`), so a
reviewer can see what was accepted and where.
