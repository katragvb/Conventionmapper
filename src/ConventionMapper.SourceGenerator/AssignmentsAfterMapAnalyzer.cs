using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>
    /// CM0023: <c>var x = s.ToY(); x.A = ...; x.B = ...;</c> right after a generated mapping method. The values can be
    /// parameters of the mapping method instead, so the object is built in one place and a forgotten value is a compile error.
    /// The IDE fix adds an overload with those parameters ([MapInherit] keeps the configuration) and rewrites the call.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AssignmentsAfterMapAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor AssignmentsAfterMap = new DiagnosticDescriptor(
            "CM0023", "Members assigned after a generated mapping",
            "{0} assigned after '{1}' can be parameters of the mapping method",
            "ConventionMapper.SourceGenerator", DiagnosticSeverity.Info, true,
            helpLinkUri: "https://github.com/katragvb/Conventionmapper/blob/main/docs/diagnostics.md#cm0023");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(AssignmentsAfterMap);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LocalDeclarationStatement);
        }

        private static void Analyze(SyntaxNodeAnalysisContext context)
        {
            var declaration = (LocalDeclarationStatementSyntax)context.Node;
            if (declaration.Declaration.Variables.Count != 1 || !(declaration.Parent is BlockSyntax block)) return;
            var variable = declaration.Declaration.Variables[0];
            if (!(variable.Initializer?.Value is InvocationExpressionSyntax invocation)) return;
            if (!(context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol called)) return;

            // Extension calls bind to the "reduced" method, which has no 'this' parameter.
            var unreduced = called.ReducedFrom ?? called;
            var method = unreduced.PartialDefinitionPart ?? unreduced;
            if (method.ReturnsVoid || method.IsGenericMethod || !IsGeneratedMapping(method)) return;
            if (!(context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is ILocalSymbol local)) return;

            var assigned = FindAssignments(block, declaration, local, method.ReturnType, context.SemanticModel, context.CancellationToken);
            if (assigned.Count == 0) return;

            var names = assigned.Select(a => a.Member.Name).ToList();
            var text = names.Count == 1 ? "'" + names[0] + "' is" : string.Join(", ", names.Select(n => "'" + n + "'")) + " are";
            context.ReportDiagnostic(Diagnostic.Create(AssignmentsAfterMap, declaration.GetLocation(),
                assigned.Select(a => a.Statement.GetLocation()),
                ImmutableDictionary<string, string?>.Empty.Add("Members", string.Join(",", names)),
                text, method.Name));
        }

        /// <summary>A partial method of a [GeneratedMapper] class (the ones the generator implements).</summary>
        internal static bool IsGeneratedMapping(IMethodSymbol method)
        {
            if (method.ContainingType == null || !method.ContainingType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == MapperGenerator.GeneratedMapperAttributeName))
                return false;
            return method.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is MethodDeclarationSyntax m &&
                                                             m.Modifiers.Any(SyntaxKind.PartialKeyword) && m.Body == null && m.ExpressionBody == null);
        }

        internal readonly struct Assignment
        {
            public Assignment(ExpressionStatementSyntax statement, ISymbol member, ExpressionSyntax value)
            {
                Statement = statement;
                Member = member;
                Value = value;
            }

            public ExpressionStatementSyntax Statement { get; }
            public ISymbol Member { get; }
            public ExpressionSyntax Value { get; }
        }

        /// <summary>The "x.Member = value;" statements directly after the declaration (stops at the first other statement).</summary>
        internal static List<Assignment> FindAssignments(BlockSyntax block, LocalDeclarationStatementSyntax declaration, ILocalSymbol local,
            ITypeSymbol destination, SemanticModel model, System.Threading.CancellationToken ct)
        {
            var result = new List<Assignment>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = block.Statements.IndexOf(declaration);
            for (var i = index + 1; i < block.Statements.Count; i++)
            {
                if (!(block.Statements[i] is ExpressionStatementSyntax statement) ||
                    !(statement.Expression is AssignmentExpressionSyntax assignment) || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                    !(assignment.Left is MemberAccessExpressionSyntax access) || !(access.Expression is IdentifierNameSyntax target))
                    break;
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(target, ct).Symbol, local)) break;
                var member = model.GetSymbolInfo(access, ct).Symbol;
                if (!(member is IPropertySymbol || member is IFieldSymbol) || member.IsStatic) break;
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, destination) && !InheritsFrom(destination, member.ContainingType)) break;
                // The value must not read the new object itself, and each member is moved once.
                if (assignment.Right.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                        .Any(id => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local))) break;
                if (!seen.Add(member.Name)) break;
                result.Add(new Assignment(statement, member, assignment.Right));
            }
            return result;
        }

        private static bool InheritsFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t, baseType)) return true;
            return false;
        }
    }
}
