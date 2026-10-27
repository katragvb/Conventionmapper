using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ConventionMapper.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ConventionMapper.Analyzers
{
    /// <summary>
    /// Build-time checks for the runtime ConventionMapper API:
    /// CM1001 reports destination members of <c>CreateMap&lt;TSource, TDestination&gt;()</c> that no source member matches,
    /// so the error shows in the IDE instead of when <c>AssertConfigurationIsValid()</c> runs.
    /// CM1002 suggests <c>Map&lt;TSource, TDestination&gt;</c> when the source type is statically known.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MappingAnalyzer : DiagnosticAnalyzer
    {
        public const string UnmappedMemberId = "CM1001";
        public const string PreferTypedMapId = "CM1002";
        public const string SensitiveLeakId = "CM1003";
        public const string MemberProperty = "Member";
        public const string SourceTypeProperty = "SourceType";

        private const string Category = "ConventionMapper";
        private const string HelpLink = "https://github.com/katragvb/Conventionmapper/blob/main/docs/diagnostics.md";

        internal static readonly DiagnosticDescriptor UnmappedMember = new DiagnosticDescriptor(
            UnmappedMemberId, "Unmapped destination member",
            "Destination member '{0}' of '{1}' has no matching source member on '{2}'; AssertConfigurationIsValid() will fail. Map it with ForMember(...) or ignore it.",
            Category, DiagnosticSeverity.Warning, true,
            "Checks CreateMap<TSource, TDestination>() calls against the default conventions (same name, Get-methods, flattening). " +
            "Maps that are configured further in the same statement, and profiles that change naming conventions, are not checked.",
            HelpLink + "#cm1001");

        internal static readonly DiagnosticDescriptor PreferTypedMap = new DiagnosticDescriptor(
            PreferTypedMapId, "Use Map<TSource, TDestination> when the source type is known",
            "The source type is known: Map<{0}, {1}>(...) uses the static fast path without a runtime type lookup",
            Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + "#cm1002");

        internal static readonly DiagnosticDescriptor SensitiveLeak = new DiagnosticDescriptor(
            SensitiveLeakId, "Sensitive member would be copied",
            "'{0}' is marked [Sensitive] and would be copied to '{1}.{2}', which is not. Ignore it with ForMember(d => d.{2}, o => o.Ignore()), or mark '{2}' [Sensitive] if it is protected too.",
            Category, DiagnosticSeverity.Error, true,
            "A mapping must not copy a [Sensitive] member (password hash, token, personal data) into a destination member that is not also marked [Sensitive]. " +
            "The same check fails the configuration at runtime.",
            HelpLink + "#cm1003");

        /// <summary>Configuration calls that change how members match; profiles using them are not checked by CM1001.</summary>
        private static readonly string[] ConventionChangingCalls =
        {
            "RecognizePrefixes", "RecognizePostfixes", "RecognizeDestinationPrefixes", "RecognizeDestinationPostfixes",
            "ReplaceMemberName", "AddGlobalIgnore", "MemberNamingConvention", "ShouldMapProperty", "ShouldMapField",
            "ShouldMapMethod", "ForAllMaps", "ForAllPropertyMaps", "IncludeMembers"
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(UnmappedMember, PreferTypedMap, SensitiveLeak);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;
            if (method.ContainingType?.ContainingNamespace?.ToDisplayString() != "ConventionMapper") return;

            if (method.Name == "CreateMap" && method.TypeArguments.Length == 2 && method.Parameters.Length == 0)
                AnalyzeCreateMap(context, invocation, method);
            else if (method.Name == "Map" && method.TypeArguments.Length == 1 && method.Parameters.Length == 1 &&
                     method.Parameters[0].Type.SpecialType == SpecialType.System_Object)
                AnalyzeUntypedMap(context, invocation, method);
        }

        private static void AnalyzeCreateMap(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method)
        {
            // Only bare "CreateMap<S, D>();" statements: anything chained (ForMember, ConvertUsing, ...) may map the members.
            if (!(invocation.Parent is IExpressionStatementOperation)) return;
            if (!(method.TypeArguments[0] is INamedTypeSymbol source) || !(method.TypeArguments[1] is INamedTypeSymbol destination)) return;
            if (destination.TypeKind != TypeKind.Class && destination.TypeKind != TypeKind.Struct) return;
            if (source.SpecialType != SpecialType.None || destination.SpecialType != SpecialType.None) return;
            if (ChangesConventions(invocation.Syntax, context)) return;

            var resolver = new MemberResolver(s => s.DeclaredAccessibility == Accessibility.Public);
            foreach (var member in PublicWritableMembers(destination))
            {
                if (HasConventionMapperAttribute(member, "IgnoreMapAttribute") || HasConventionMapperAttribute(member, "SourceMemberAttribute")) continue;
                var path = resolver.FindPath(source, member.Name);
                if (path != null)
                {
                    if (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(member))
                        context.ReportDiagnostic(Diagnostic.Create(SensitiveLeak, invocation.Syntax.GetLocation(),
                            ImmutableDictionary<string, string?>.Empty.Add(MemberProperty, member.Name),
                            source.Name + "." + string.Join(".", path.Select(m => m.Name)), destination.Name, member.Name));
                    continue;
                }

                var properties = ImmutableDictionary<string, string?>.Empty.Add(MemberProperty, member.Name);
                context.ReportDiagnostic(Diagnostic.Create(UnmappedMember, invocation.Syntax.GetLocation(), properties,
                    member.Name, destination.Name, source.Name));
            }
        }

        private static bool ChangesConventions(SyntaxNode syntax, OperationAnalysisContext context)
        {
            var type = syntax.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (type == null) return false;
            var text = type.ToString();
            return ConventionChangingCalls.Any(call => text.IndexOf(call, StringComparison.Ordinal) >= 0);
        }

        private static IEnumerable<ISymbol> PublicWritableMembers(INamedTypeSymbol type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (INamedTypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member.IsStatic || !member.CanBeReferencedByName || member.DeclaredAccessibility != Accessibility.Public) continue;
                    var writable = member is IPropertySymbol p
                        ? !p.IsIndexer && p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public
                        : member is IFieldSymbol f && !f.IsReadOnly && !f.IsConst;
                    if (writable && names.Add(member.Name)) yield return member;
                }
            }
        }

        private static bool HasConventionMapperAttribute(ISymbol member, string name) =>
            member.GetAttributes().Any(a => a.AttributeClass?.Name == name && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "ConventionMapper");

        private static void AnalyzeUntypedMap(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method)
        {
            if (invocation.Arguments.Length != 1) return;
            var value = invocation.Arguments[0].Value;
            var sourceType = value is IConversionOperation conversion && conversion.IsImplicit ? conversion.Operand.Type : value.Type;
            if (sourceType == null || sourceType.SpecialType == SpecialType.System_Object || sourceType.TypeKind == TypeKind.Dynamic) return;
            // Only types whose runtime type equals the static type, so the typed overload maps exactly the same way.
            if (!sourceType.IsValueType && !sourceType.IsSealed) return;

            var syntax = invocation.Syntax as InvocationExpressionSyntax;
            var name = syntax?.Expression is MemberAccessExpressionSyntax access ? access.Name : syntax?.Expression as SimpleNameSyntax;
            if (!(name is GenericNameSyntax generic)) return;

            var properties = ImmutableDictionary<string, string?>.Empty.Add(SourceTypeProperty, sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            context.ReportDiagnostic(Diagnostic.Create(PreferTypedMap, generic.GetLocation(), properties,
                sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                method.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }
}
