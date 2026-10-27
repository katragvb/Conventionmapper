using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace ConventionMapper.SourceGenerator.CodeFixes
{
    /// <summary>
    /// CM0023: turns <c>var x = s.ToY(); x.A = a; x.B = b;</c> into <c>var x = s.ToY(a, b);</c>. Adds the overload
    /// <c>[MapInherit(nameof(ToY))] Y ToY(S s, A a, B b)</c> to the mapper (unless one with those parameters exists), so other
    /// callers keep working and the configuration is shared. When ToY is already overloaded, the attribute names it by its
    /// parameter types: <c>[MapInherit(nameof(ToY), typeof(S))]</c>.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AssignmentsToParametersCodeFixProvider))]
    public sealed class AssignmentsToParametersCodeFixProvider : CodeFixProvider
    {
        private const string Id = "CM0023";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Id);

        // Each fix may edit the shared mapper declaration, so fixes are not batched.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                if (!(root.FindNode(diagnostic.Location.SourceSpan) is LocalDeclarationStatementSyntax declaration) ||
                    !(declaration.Declaration.Variables.FirstOrDefault()?.Initializer?.Value is InvocationExpressionSyntax invocation) ||
                    !(model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol called))
                    continue;
                // Extension calls bind to the "reduced" method, which has no 'this' parameter.
                var unreduced = called.ReducedFrom ?? called;
                var method = unreduced.PartialDefinitionPart ?? unreduced;
                var statements = diagnostic.AdditionalLocations
                    .Select(l => root.FindNode(l.SourceSpan) as ExpressionStatementSyntax)
                    .Where(s => s != null).Select(s => s!).ToList();
                if (statements.Count == 0) continue;
                var definition = method.DeclaringSyntaxReferences.FirstOrDefault(r => r.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax m && m.Body == null && m.ExpressionBody == null);
                if (definition == null || context.Document.Project.Solution.GetDocument(definition.SyntaxTree) == null) continue;

                context.RegisterCodeFix(
                    CodeAction.Create("Pass the assigned values to '" + method.Name + "'",
                        ct => MoveToParametersAsync(context.Document, invocation, method, definition, statements, ct),
                        Id),
                    diagnostic);
            }
        }

        private static async Task<Solution> MoveToParametersAsync(Document document, InvocationExpressionSyntax invocation, IMethodSymbol method,
            SyntaxReference definition, List<ExpressionStatementSyntax> statements, CancellationToken ct)
        {
            var solution = document.Project.Solution;
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model == null) return solution;

            // The members and values, in assignment order.
            var moved = new List<(ISymbol Member, ITypeSymbol Type, ExpressionSyntax Value)>();
            foreach (var statement in statements)
            {
                var assignment = (AssignmentExpressionSyntax)statement.Expression;
                var member = model.GetSymbolInfo(assignment.Left, ct).Symbol;
                if (member == null) return solution;
                var type = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
                moved.Add((member, type, assignment.Right));
            }

            var mapperDocument = solution.GetDocument(definition.SyntaxTree)!;
            var declaration = (MethodDeclarationSyntax)await definition.GetSyntaxAsync(ct).ConfigureAwait(false);
            var editor = new SolutionEditor(solution);

            // An overload with the existing parameters followed by the new ones, or a new one that inherits the configuration.
            var existing = method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>().FirstOrDefault(m =>
                m.Parameters.Length == method.Parameters.Length + moved.Count &&
                m.Parameters.Take(method.Parameters.Length).Select(x => x.Type).SequenceEqual(method.Parameters.Select(x => x.Type), SymbolEqualityComparer.Default) &&
                m.Parameters.Skip(method.Parameters.Length).Select(x => x.Type).SequenceEqual(moved.Select(x => x.Type), SymbolEqualityComparer.Default));
            if (existing == null)
            {
                var mapperEditor = await editor.GetDocumentEditorAsync(mapperDocument.Id, ct).ConfigureAwait(false);
                var mapperModel = await mapperDocument.GetSemanticModelAsync(ct).ConfigureAwait(false);
                var used = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                var parameters = new List<ParameterSyntax>();
                foreach (var item in moved)
                {
                    var name = Camel(item.Member.Name);
                    while (!used.Add(name)) name += "Value";
                    var typeName = mapperModel != null
                        ? item.Type.ToMinimalDisplayString(mapperModel, declaration.SpanStart)
                        : item.Type.ToDisplayString();
                    parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name))
                        .WithType(SyntaxFactory.ParseTypeName(typeName)));
                }

                var overload = declaration
                    .WithAttributeLists(default)
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(SyntaxHelpers.Indentation(declaration)))
                    .WithParameterList(declaration.ParameterList.AddParameters(parameters.ToArray()));
                // When the mapping method already has overloads, [MapInherit] must name it by its parameter types (CM0019).
                var overloaded = method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>().Count() > 1;
                var inheritArguments = "nameof(" + method.Name + ")";
                if (overloaded)
                    foreach (var p in method.Parameters)
                    {
                        var type = p.Type.IsReferenceType ? p.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : p.Type;
                        inheritArguments += ", typeof(" + (mapperModel != null ? type.ToMinimalDisplayString(mapperModel, declaration.SpanStart) : type.ToDisplayString()) + ")";
                    }
                overload = SyntaxHelpers.AddAttribute(overload, SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("MapInherit"),
                    SyntaxFactory.ParseAttributeArgumentList("(" + inheritArguments + ")")));
                overload = overload.WithLeadingTrivia(new[] { SyntaxFactory.EndOfLine("\n") }.Concat(overload.GetLeadingTrivia()))
                    .WithAdditionalAnnotations(Formatter.Annotation);
                mapperEditor.InsertAfter(declaration, overload);
            }

            // The call takes the values; the assignments go.
            var callEditor = await editor.GetDocumentEditorAsync(document.Id, ct).ConfigureAwait(false);
            var arguments = moved.Select(m => SyntaxFactory.Argument(m.Value.WithoutTrivia()));
            callEditor.ReplaceNode(invocation, invocation.WithArgumentList(invocation.ArgumentList.AddArguments(arguments.ToArray())));
            foreach (var statement in statements) callEditor.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia);
            return editor.GetChangedSolution();
        }

        private static string Camel(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
