using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>
    /// CM0029: an object initializer (or a conversion operator) that copies at least three same-name members from one
    /// object by hand: <c>new OrderDto { Id = order.Id, Name = order.Name, Total = order.Total }</c>. The IDE fix turns it
    /// into a generated mapping method in explicit mode; for an operator it can also remove the operator and update its
    /// call sites. The threshold is <c>conventionmapper.copy_min_members</c> in .editorconfig. Raise the severity
    /// (<c>dotnet_diagnostic.CM0029.severity = warning</c>) to list every migration candidate in the build output.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandWrittenCopyAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor HandWrittenCopy = new DiagnosticDescriptor(
            "CM0029", "Hand-written copy can be a generated mapping",
            "{0} copies {1} members from '{2}' by hand; it can be a generated mapping method",
            "ConventionMapper.SourceGenerator", DiagnosticSeverity.Info, true,
            helpLinkUri: "https://github.com/katragvb/Conventionmapper/blob/main/docs/source-generator.md#migrating-hand-written-copies");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(HandWrittenCopy);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        }

        private static void Analyze(SyntaxNodeAnalysisContext context)
        {
            var creation = (BaseObjectCreationExpressionSyntax)context.Node;
            // Cheap syntax checks before binding: enough "X = y.X" entries to be worth a look.
            if (creation.Initializer == null || creation.Initializer.Expressions.Count < 2) return;
            var minimum = MinimumMembers(context);
            var sameName = creation.Initializer.Expressions.Count(e => e is AssignmentExpressionSyntax a && a.Left is IdentifierNameSyntax left &&
                                                                       a.Right is MemberAccessExpressionSyntax right && right.Name.Identifier.ValueText == left.Identifier.ValueText);
            if (sameName < minimum) return;

            var plan = CopyAnalysis.Analyze(creation, context.SemanticModel, context.CancellationToken);
            if (plan == null || plan.Convention.Count < minimum) return;
            // A generated method needs types it can name from a top-level class.
            if (!Nameable(plan.Destination) || !Nameable(plan.SourceType)) return;
            if (!(creation.FirstAncestorOrSelf<TypeDeclarationSyntax>() is TypeDeclarationSyntax)) return;

            var count = plan.Convention.Count + plan.Renamed.Count;
            Location location;
            string subject;
            var properties = ImmutableDictionary<string, string?>.Empty;
            if (plan.OperatorDeclaration != null)
            {
                var op = plan.OperatorDeclaration;
                location = Location.Create(op.SyntaxTree, TextSpan.FromBounds(op.ImplicitOrExplicitKeyword.SpanStart, op.ParameterList.Span.End));
                subject = "The conversion operator to '" + plan.Destination.Name + "'";
                properties = properties.Add("Operator", "true");
            }
            else
            {
                var end = creation is ObjectCreationExpressionSyntax named ? named.Type.Span.End : creation.NewKeyword.Span.End;
                location = Location.Create(creation.SyntaxTree, TextSpan.FromBounds(creation.NewKeyword.SpanStart, end));
                subject = "This object initializer";
            }
            context.ReportDiagnostic(Diagnostic.Create(HandWrittenCopy, location, new[] { creation.GetLocation() }, properties,
                subject, count, plan.Source.Name));
        }

        private static int MinimumMembers(SyntaxNodeAnalysisContext context)
        {
            var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(context.Node.SyntaxTree);
            return options.TryGetValue(CopyAnalysis.MinimumMembersOption, out var text) && int.TryParse(text, out var value) && value > 0
                ? value
                : CopyAnalysis.DefaultMinimumMembers;
        }

        /// <summary>Not private or protected anywhere up the nesting chain, so a mapper class in the namespace can use it.</summary>
        internal static bool Nameable(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) return Nameable(array.ElementType);
            for (var t = type as INamedTypeSymbol; t != null; t = t.ContainingType)
            {
                if (t.DeclaredAccessibility == Accessibility.Private || t.DeclaredAccessibility == Accessibility.Protected ||
                    t.DeclaredAccessibility == Accessibility.ProtectedAndInternal) return false;
                if (t.TypeArguments.Any(a => a is ITypeParameterSymbol || !Nameable(a))) return false;
            }
            return true;
        }
    }
}
