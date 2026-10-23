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
            "Mapping method '{0}' must be a non-generic partial method that either takes a source and returns the destination, or takes a source and a target and returns void",
            Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnmappedEnumValue = new DiagnosticDescriptor(
            "CM0007", "Unmapped enum value",
            "Enum value '{0}.{1}' has no member with the same name in '{2}'; mapping it throws ArgumentOutOfRangeException",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);

        public static readonly DiagnosticDescriptor UnknownAttributeMember = new DiagnosticDescriptor(
            "CM0008", "Unknown member in mapping attribute",
            "'{0}' in [{1}] on '{2}' does not match any member",
            Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink);
    }
}
