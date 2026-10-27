using Microsoft.CodeAnalysis;

namespace ConventionMapper.SourceGenerator
{
    internal static class Diagnostics
    {
        private const string Category = "ConventionMapper.SourceGenerator";
        private const string HelpLink = "https://github.com/katragvb/Conventionmapper/blob/main/docs/diagnostics.md";

        public static readonly DiagnosticDescriptor UnmappedMember = new DiagnosticDescriptor(
            "CM0001", "Unmapped destination member",
            "Destination member '{0}' on '{1}' has no matching source member on '{2}'. Map it with [MapMember(\"...\", \"{0}\")] or skip it with [MapIgnore(\"{0}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0001");

        public static readonly DiagnosticDescriptor IncompatibleTypes = new DiagnosticDescriptor(
            "CM0002", "Member types cannot be mapped",
            "Cannot map '{0}' ({1}) to '{2}' ({3}). Declare a mapping method for these types, map another member with [MapMember], or skip it with [MapIgnore(\"{4}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0002");

        public static readonly DiagnosticDescriptor NullableToNonNullable = new DiagnosticDescriptor(
            "CM0003", "Nullable source mapped to a non-nullable destination",
            "'{0}' may be null but destination '{1}' is not nullable; null becomes the default value",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + "#cm0003");

        public static readonly DiagnosticDescriptor NoUsableConstructor = new DiagnosticDescriptor(
            "CM0004", "No usable constructor",
            "'{0}' has no accessible constructor whose parameters can all be mapped from '{1}'",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0004");

        public static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "CM0005", "Mapper must be partial",
            "'{0}' and every type that contains it must be declared partial to use [GeneratedMapper]",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0005");

        public static readonly DiagnosticDescriptor UnsupportedSignature = new DiagnosticDescriptor(
            "CM0006", "Unsupported mapping method",
            "Mapping method '{0}' must be a non-generic partial method that either takes a source (and optional extra values) and returns the destination, or takes a source and a target (and optional extra values) and returns void",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0006");

        public static readonly DiagnosticDescriptor UnmappedEnumValue = new DiagnosticDescriptor(
            "CM0007", "Unmapped enum value",
            "Enum value '{0}.{1}' has no member with the same name in '{2}'; mapping it throws ArgumentOutOfRangeException",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0007");

        public static readonly DiagnosticDescriptor SensitiveLeak = new DiagnosticDescriptor(
            "CM0009", "Sensitive member would be copied",
            "'{0}' is marked [Sensitive] and would be copied to '{1}', which is not. Skip it with [MapIgnore(\"{2}\")], or mark '{1}' [Sensitive] if it is protected too.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0009");

        public static readonly DiagnosticDescriptor RecursiveProjection = new DiagnosticDescriptor(
            "CM0010", "Recursive type in a projection",
            "'{0}' refers back to itself; a projection cannot expand it, so the member is left null. Skip it with [MapIgnore] or map it in a separate query.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0010");

        public static readonly DiagnosticDescriptor UnusedParameter = new DiagnosticDescriptor(
            "CM0011", "Mapping method parameter is not used",
            "Parameter '{0}' of '{1}' does not match any destination member. Rename it to the member it sets, map it with [MapMember(\"{0}\", \"...\")], or remove it.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0011");

        public static readonly DiagnosticDescriptor NullSubstituteNeverNull = new DiagnosticDescriptor(
            "CM0012", "Null substitute has no effect",
            "The source of '{0}' ({1}) is never null, so [MapNullSubstitute(\"{0}\", ...)] has no effect",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0012");

        public static readonly DiagnosticDescriptor InvalidNullSubstitute = new DiagnosticDescriptor(
            "CM0013", "Invalid null substitute",
            "The null substitute for '{0}' must be a constant of a type that converts implicitly to {1}",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0013");

        public static readonly DiagnosticDescriptor AmbiguousMapMemberSource = new DiagnosticDescriptor(
            "CM0014", "Ambiguous [MapMember] source",
            "Source path '{0}' for '{1}' matches both the source member '{2}' and the parameter '{3}' (names are case-insensitive). Write the name in its exact case (\"{3}\" for the parameter), or \"{4}\" for the source member.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0014");

        public static readonly DiagnosticDescriptor AmbiguousSource = new DiagnosticDescriptor(
            "CM0015", "Ambiguous source for a destination member",
            "Destination member '{0}' on '{1}' could be mapped from {2}. Choose one with [MapMember(\"...\", \"{0}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0015");

        public static readonly DiagnosticDescriptor LossyConversion = new DiagnosticDescriptor(
            "CM0016", "Conversion can fail or lose data at runtime",
            "'{0}' is converted to '{1}' with {2}, which can throw or lose data at runtime. If this is intended, allow it with [MapAllowParse(\"{3}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0016");

        public static readonly DiagnosticDescriptor UnmarkedConversionMethod = new DiagnosticDescriptor(
            "CM0017", "Static method used as a conversion",
            "Static method '{0}' converts {1} and is used as a conversion for {2}. Mark it [MapConversion] if that is intended, or move it out of the mapper class.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0017");

        public static readonly DiagnosticDescriptor InvalidMapConversion = new DiagnosticDescriptor(
            "CM0018", "Invalid [MapConversion] method",
            "[MapConversion] method '{0}' must be a non-generic static method of the mapper class that takes one value and returns another",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0018");

        public static readonly DiagnosticDescriptor InvalidMapInherit = new DiagnosticDescriptor(
            "CM0019", "Invalid [MapInherit]",
            "[MapInherit(\"{0}\")] on '{1}': {2}",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0019");

        public static readonly DiagnosticDescriptor UseNameof = new DiagnosticDescriptor(
            "CM0020", "Member name can use nameof",
            "Use nameof({0}) instead of \"{1}\" so a rename updates the attribute",
            Category, DiagnosticSeverity.Hidden, true, helpLinkUri: HelpLink + "#cm0020");

        public static readonly DiagnosticDescriptor ProjectionClientEvaluation = new DiagnosticDescriptor(
            "CM0021", "Projection may not translate to SQL",
            "In projection '{0}', '{1}' uses {2}, which {3}. Map it after the query, add a computed column, or skip it with [MapIgnore(\"{4}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0021");

        public static readonly DiagnosticDescriptor UnknownProvider = new DiagnosticDescriptor(
            "CM0022", "Unknown projection provider",
            "Provider '{0}' is not known; use one of {1}. Projections are checked with the default rules.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0022");

        public static readonly DiagnosticDescriptor StaleGenerator = new DiagnosticDescriptor(
            "CM0024", "Out-of-date source generator is running",
            "ConventionMapper.SourceGenerator {0} is running, but the project references {1}. Restart the IDE (or its C# language server) so it loads the new generator; until then the generated code is from the old version and can cause errors such as ambiguous calls.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0024");

        public static readonly DiagnosticDescriptor InvalidValueAttribute = new DiagnosticDescriptor(
            "CM0025", "Invalid member value attribute",
            "[{0}] for '{1}' on '{2}': {3}",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + "#cm0025");

        public static readonly DiagnosticDescriptor SuggestSensitive = new DiagnosticDescriptor(
            "CM0026", "Member looks like personal data",
            "'{0}' looks like personal data and is copied by a mapping; mark it [Sensitive] so it can only flow into protected members",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + "#cm0026");

        public static readonly DiagnosticDescriptor PossibleTruncation = new DiagnosticDescriptor(
            "CM0027", "String can be longer than the destination allows",
            "'{0}' can be longer than {1} characters, the maximum of '{2}'. Cut it with [MapTruncate(\"{3}\")], or acknowledge it with [MapReviewed(\"{3}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0027");

        public static readonly DiagnosticDescriptor PossibleTruncationInfo = new DiagnosticDescriptor(
            "CM0027", "String can be longer than the destination allows",
            "'{0}' can be longer than {1} characters, the maximum of '{2}'. Cut it with [MapTruncate(\"{3}\")], or acknowledge it with [MapReviewed(\"{3}\")].",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + "#cm0027");

        public static readonly DiagnosticDescriptor DatabaseGeneratedWrite = new DiagnosticDescriptor(
            "CM0028", "Mapping writes a database-generated value or a navigation",
            "'{0}' writes {1} '{2}' of the entity '{3}'. The database sets it (or EF Core attaches it), so copying it between rows causes duplicate keys, failed or ignored writes to computed columns, or accidental graph changes: leave it out with [MapIgnore(\"{2}\")], or acknowledge it with [MapReviewed(\"{2}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0028");

        public static readonly DiagnosticDescriptor CultureSensitive = new DiagnosticDescriptor(
            "CM0030", "Conversion depends on the server's regional settings",
            "'{0}' uses {1}, which reads or writes text in the current culture, so the result changes with the server's regional settings. Pass CultureInfo.InvariantCulture, or use [MapFormat] on the member.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0030");

        public static readonly DiagnosticDescriptor ObsoleteMember = new DiagnosticDescriptor(
            "CM0031", "Mapping uses an obsolete member",
            "'{0}' {1} '{2}', which is [Obsolete]{3}",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0031");

        public static readonly DiagnosticDescriptor CopyDocumentation = new DiagnosticDescriptor(
            "CM0033", "Destination member can take the source member's documentation",
            "'{0}' has no documentation; it is copied from '{1}', which has",
            Category, DiagnosticSeverity.Hidden, true, helpLinkUri: HelpLink + "#cm0033");

        public static readonly DiagnosticDescriptor DatabaseDefaultReplacesValue = new DiagnosticDescriptor(
            "CM0034", "Database default replaces the copied value",
            "'{0}' writes '{1}' of the entity '{2}', a {3} with the database default {4}. EF Core leaves the column out of the insert when the value is {5}, so the row gets {4} instead of the copied {5}. Make the property nullable, configure HasSentinel, or acknowledge it with [MapReviewed(\"{1}\", For = ReviewKind.DatabaseDefault)].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0034");

        public static readonly DiagnosticDescriptor StaleReview = new DiagnosticDescriptor(
            "CM0035", "[MapReviewed] acknowledges nothing",
            "[MapReviewed({0})] on '{1}' acknowledges nothing: no method that uses it writes '{2}' as a key, a database-generated value, a navigation, a column with a database default or a string that can be too long (of the kinds it names). Remove it.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0035");

        public static readonly DiagnosticDescriptor DatabaseDefaultReplacesNull = new DiagnosticDescriptor(
            "CM0036", "Database default replaces a copied null",
            "'{0}' writes '{1}' of the entity '{2}' from a source that can be null, and the column has the database default {3}: EF Core leaves null out of the insert, so a copied null is stored as {3}. If that is intended, acknowledge it with [MapReviewed(\"{1}\", For = ReviewKind.DatabaseDefault)]; to store null, configure a sentinel (HasSentinel) or drop the default.",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + "#cm0036");

        public static readonly DiagnosticDescriptor UnknownAttributeMember = new DiagnosticDescriptor(
            "CM0008", "Unknown member in mapping attribute",
            "'{0}' in [{1}] on '{2}' does not match any member",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + "#cm0008");
    }
}
