# 0009. Generated conversions use the invariant culture

**Status:** Accepted

## Context

`decimal.Parse("1.5")` and `date.ToString()` depend on the server's regional settings. The bug appears only on machines with a
different culture, typically after deployment.

## Decision

Every conversion the generator writes between text and numbers, dates or other scalars uses `CultureInfo.InvariantCulture`.
`[MapFormat("EffectiveDate", "yyyy-MM-dd")]` fixes a format per member (with `ParseExact` in the other direction). Conversion and
`[MapFrom]` methods written by hand that use the current culture are warning CM0030.

## Consequences

The same input gives the same output on every machine. Apps that need culture-specific display text format it in the UI layer,
not in a mapping.
