using Microsoft.CodeAnalysis;

namespace ConventionMapper.SourceGenerator
{
    internal static class Diagnostics
    {
        private const string Category = "ConventionMapper.SourceGenerator";
        private const string HelpLink = "https://github.com/katragvb/Conventionmapper/blob/main/docs/source-generator.md";

        public static readonly DiagnosticDescriptor UnmappedMember = new DiagnosticDescriptor(
            "CM0001", "Unmapped destination member",
            "Destination member '{0}' on '{1}' has no matching source member on '{2}'. Map it with [MapMember(\"...\", \"{0}\")] or skip it with [MapIgnore(\"{0}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor IncompatibleTypes = new DiagnosticDescriptor(
            "CM0002", "Member types cannot be mapped",
            "Cannot map '{0}' ({1}) to '{2}' ({3}). Declare a mapping method for these types, map another member with [MapMember], or skip it with [MapIgnore(\"{4}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor NullableToNonNullable = new DiagnosticDescriptor(
            "CM0003", "Nullable source mapped to a non-nullable destination",
            "'{0}' may be null but destination '{1}' is not nullable; null becomes the default value",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor NoUsableConstructor = new DiagnosticDescriptor(
            "CM0004", "No usable constructor",
            "'{0}' has no accessible constructor whose parameters can all be mapped from '{1}'",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor NotPartial = new DiagnosticDescriptor(
            "CM0005", "Mapper must be partial",
            "'{0}' and every type that contains it must be declared partial to use [GeneratedMapper]",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnsupportedSignature = new DiagnosticDescriptor(
            "CM0006", "Unsupported mapping method",
            "Mapping method '{0}' must be a non-generic partial method that either takes a source (and optional extra values) and returns the destination, or takes a source and a target (and optional extra values) and returns void",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnmappedEnumValue = new DiagnosticDescriptor(
            "CM0007", "Unmapped enum value",
            "Enum value '{0}.{1}' has no member with the same name in '{2}'; mapping it throws ArgumentOutOfRangeException",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor SensitiveLeak = new DiagnosticDescriptor(
            "CM0009", "Sensitive member would be copied",
            "'{0}' is marked [Sensitive] and would be copied to '{1}', which is not. Skip it with [MapIgnore(\"{2}\")], or mark '{1}' [Sensitive] if it is protected too.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor RecursiveProjection = new DiagnosticDescriptor(
            "CM0010", "Recursive type in a projection",
            "'{0}' refers back to itself; a projection cannot expand it, so the member is left null. Skip it with [MapIgnore] or map it in a separate query.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnusedParameter = new DiagnosticDescriptor(
            "CM0011", "Mapping method parameter is not used",
            "Parameter '{0}' of '{1}' does not match any destination member. Rename it to the member it sets, map it with [MapMember(\"{0}\", \"...\")], or remove it.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor NullSubstituteNeverNull = new DiagnosticDescriptor(
            "CM0012", "Null substitute has no effect",
            "The source of '{0}' ({1}) is never null, so [MapNullSubstitute(\"{0}\", ...)] has no effect",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InvalidNullSubstitute = new DiagnosticDescriptor(
            "CM0013", "Invalid null substitute",
            "The null substitute for '{0}' must be a constant of a type that converts implicitly to {1}",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor AmbiguousMapMemberSource = new DiagnosticDescriptor(
            "CM0014", "Ambiguous [MapMember] source",
            "Source path '{0}' for '{1}' matches both the source member '{2}' and the parameter '{3}' (names are case-insensitive). Write the name in its exact case (\"{3}\" for the parameter), or \"{4}\" for the source member.",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor AmbiguousSource = new DiagnosticDescriptor(
            "CM0015", "Ambiguous source for a destination member",
            "Destination member '{0}' on '{1}' could be mapped from {2}. Choose one with [MapMember(\"...\", \"{0}\")].",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor LossyConversion = new DiagnosticDescriptor(
            "CM0016", "Conversion can fail or lose data at runtime",
            "'{0}' is converted to '{1}' with {2}, which can throw or lose data at runtime. If this is intended, allow it with [MapAllowParse(\"{3}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnmarkedConversionMethod = new DiagnosticDescriptor(
            "CM0017", "Static method used as a conversion",
            "Static method '{0}' converts {1} and is used as a conversion for {2}. Mark it [MapConversion] if that is intended, or move it out of the mapper class.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InvalidMapConversion = new DiagnosticDescriptor(
            "CM0018", "Invalid [MapConversion] method",
            "[MapConversion] method '{0}' must be a non-generic static method of the mapper class that takes one value and returns another",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor InvalidMapInherit = new DiagnosticDescriptor(
            "CM0019", "Invalid [MapInherit]",
            "[MapInherit(\"{0}\")] on '{1}': {2}",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UseNameof = new DiagnosticDescriptor(
            "CM0020", "Member name can use nameof",
            "Use nameof({0}) instead of \"{1}\" so a rename updates the attribute",
            Category, DiagnosticSeverity.Hidden, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor ProjectionClientEvaluation = new DiagnosticDescriptor(
            "CM0021", "Projection may not translate to SQL",
            "In projection '{0}', '{1}' uses {2}, which {3}. Map it after the query, add a computed column, or skip it with [MapIgnore(\"{4}\")].",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnknownProvider = new DiagnosticDescriptor(
            "CM0022", "Unknown projection provider",
            "Provider '{0}' is not known; use one of {1}. Projections are checked with the default rules.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor StaleGenerator = new DiagnosticDescriptor(
            "CM0024", "Out-of-date source generator is running",
            "ConventionMapper.SourceGenerator {0} is running, but the project references {1}. Restart the IDE (or its C# language server) so it loads the new generator; until then the generated code is from the old version and can cause errors such as ambiguous calls.",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnknownAttributeMember = new DiagnosticDescriptor(
            "CM0008", "Unknown member in mapping attribute",
            "'{0}' in [{1}] on '{2}' does not match any member",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);
    }
}
