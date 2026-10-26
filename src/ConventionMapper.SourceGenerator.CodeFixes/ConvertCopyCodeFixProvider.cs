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
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;

namespace ConventionMapper.SourceGenerator.CodeFixes
{
    /// <summary>
    /// CM0029 (hand-written copy): "Convert to generated mapping" replaces <c>new D { A = s.A, ... }</c> with a call to a new
    /// <c>[MapExplicit(...)] partial D ToD(S source, ...)</c> in a [GeneratedMapper] class of the same file (an existing one,
    /// or <c>DMappings</c> next to the type). Same-name members are listed in [MapExplicit], other source members get
    /// [MapMember], constants [MapConstant], and any other value becomes a parameter, so its expression stays at the call site.
    /// For a conversion operator it also offers "Replace the operator with a generated mapping", which removes the operator
    /// and rewrites its implicit and explicit uses across the solution (when every use can be rewritten).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConvertCopyCodeFixProvider))]
    public sealed class ConvertCopyCodeFixProvider : CodeFixProvider
    {
        private const string Id = "CM0029";
        private const string GeneratedMapperName = "ConventionMapper.GeneratedMapperAttribute";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Id);

        // Each fix may add to the same mapper class, so fixes are not batched.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                var location = diagnostic.AdditionalLocations.FirstOrDefault();
                if (location == null || !(root.FindNode(location.SourceSpan, getInnermostNodeForTie: true) is BaseObjectCreationExpressionSyntax creation)) continue;
                var plan = CopyAnalysis.Analyze(creation, model, context.CancellationToken);
                if (plan == null) continue;

                context.RegisterCodeFix(
                    CodeAction.Create("Convert to generated mapping", ct => ConvertAsync(context.Document, creation.Span, removeOperator: false, ct), Id + ".Convert"),
                    diagnostic);
                if (plan.Operator != null && plan.Parameters.Count == 0)
                    context.RegisterCodeFix(
                        CodeAction.Create("Replace the operator with a generated mapping (update call sites)",
                            ct => ConvertAsync(context.Document, creation.Span, removeOperator: true, ct), Id + ".ReplaceOperator"),
                        diagnostic);
            }
        }

        private static async Task<Solution> ConvertAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan span, bool removeOperator, CancellationToken ct)
        {
            var solution = document.Project.Solution;
            var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (root == null || model == null || !(root.FindNode(span, getInnermostNodeForTie: true) is BaseObjectCreationExpressionSyntax creation)) return solution;
            var plan = CopyAnalysis.Analyze(creation, model, ct);
            if (plan == null) return solution;

            // The [GeneratedMapper] class this file already has, or a new "<Destination>Mappings" after the enclosing type.
            var topLevel = creation.Ancestors().OfType<BaseTypeDeclarationSyntax>().LastOrDefault();
            if (topLevel == null) return solution;
            var mapperClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c =>
                c.Modifiers.Any(SyntaxKind.StaticKeyword) && c.Modifiers.Any(SyntaxKind.PartialKeyword) && c.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax &&
                model.GetDeclaredSymbol(c, ct) is INamedTypeSymbol t && t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == GeneratedMapperName));
            var mapperSymbol = mapperClass != null ? model.GetDeclaredSymbol(mapperClass, ct) : null;
            var position = mapperClass?.SpanStart ?? topLevel.SpanStart;
            var ns = model.GetDeclaredSymbol(topLevel, ct)?.ContainingNamespace;

            string mapperName;
            if (mapperSymbol != null) mapperName = mapperSymbol.Name;
            else
            {
                mapperName = plan.Destination.Name + "Mappings";
                for (var i = 2; ns != null && ns.GetTypeMembers(mapperName).Any(); i++) mapperName = plan.Destination.Name + "Mappings" + i;
            }

            // A method name the class does not use yet.
            var taken = new HashSet<string>(mapperSymbol?.GetMembers().Select(m => m.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var methodName = "To" + plan.Destination.Name;
            if (taken.Contains(methodName)) methodName = "To" + plan.Destination.Name + "From" + plan.SourceType.Name;
            for (var i = 2; taken.Contains(methodName); i++) methodName = "To" + plan.Destination.Name + i;

            var method = BuildMethod(plan, model, position, methodName, out var parameterNames);
            var mapperType = mapperSymbol != null ? null : (Accessible(plan.Destination) && Accessible(plan.SourceType) ? "public" : "internal");

            // The call: Mappings.ToD(source, a: ..., b: ...).
            var call = Call(mapperName, methodName, SyntaxFactory.IdentifierName(plan.Source.Name),
                plan.Parameters.Select((p, i) => (parameterNames[i], p.Value)));

            var editor = new SolutionEditor(solution);
            var documentEditor = await editor.GetDocumentEditorAsync(document.Id, ct).ConfigureAwait(false);

            List<(Document Document, SyntaxNode Node, ExpressionSyntax Operand)>? uses = null;
            if (removeOperator && plan.Operator != null && plan.OperatorDeclaration != null)
                uses = await FindOperatorUsesAsync(solution, plan.Operator, ct).ConfigureAwait(false);

            if (uses != null)
            {
                documentEditor.RemoveNode(plan.OperatorDeclaration!, SyntaxRemoveOptions.KeepNoTrivia);
                var nsName = ns != null && !ns.IsGlobalNamespace ? ns.ToDisplayString() : null;
                foreach (var group in uses.GroupBy(u => u.Document.Id))
                {
                    var useEditor = await editor.GetDocumentEditorAsync(group.Key, ct).ConfigureAwait(false);
                    foreach (var use in group)
                    {
                        var qualified = Qualify(use.Node, nsName, mapperName);
                        useEditor.ReplaceNode(use.Node, (current, _) =>
                        {
                            // Nested uses were rewritten first; take the current operand from the updated node.
                            var currentOperand = current is CastExpressionSyntax cast ? cast.Expression : (ExpressionSyntax)current;
                            return Call(qualified, methodName, currentOperand.WithoutTrivia(), Enumerable.Empty<(string, ExpressionSyntax)>())
                                .WithTriviaFrom(current);
                        });
                    }
                }
            }
            else
            {
                documentEditor.ReplaceNode(creation, call.WithTriviaFrom(creation));
            }

            if (mapperClass != null)
            {
                documentEditor.ReplaceNode(mapperClass, (current, _) =>
                {
                    var c = (ClassDeclarationSyntax)current;
                    return c.AddMembers(method);
                });
            }
            else
            {
                var newClass = SyntaxFactory.ParseMemberDeclaration(
                    "[GeneratedMapper]\n" + mapperType + " static partial class " + mapperName + "\n{\n}\n")!;
                newClass = ((ClassDeclarationSyntax)newClass).AddMembers(method)
                    .WithLeadingTrivia(SyntaxFactory.EndOfLine("\n"))
                    .WithAdditionalAnnotations(Formatter.Annotation);
                documentEditor.InsertAfter(topLevel, newClass);
            }

            if (!Imports(root, creation))
                documentEditor.ReplaceNode(root, (current, _) => ((CompilationUnitSyntax)current).AddUsings(
                    SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("ConventionMapper")).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"))));
            return editor.GetChangedSolution();
        }

        private static MethodDeclarationSyntax BuildMethod(CopyPlan plan, SemanticModel model, int position, string methodName, out List<string> parameterNames)
        {
            var destination = plan.Destination.ToMinimalDisplayString(model, position);
            var attributes = new List<string>();
            if (plan.Convention.Count > 0)
                attributes.Add("[MapExplicit(" + string.Join(", ", plan.Convention.Select(m => "nameof(" + destination + "." + m + ")")) + ")]");
            else
                attributes.Add("[MapExplicit]");
            foreach (var (path, member) in plan.Renamed)
                attributes.Add("[MapMember(\"" + path + "\", nameof(" + destination + "." + member + "))]");
            foreach (var (member, value) in plan.Constants)
                attributes.Add("[MapConstant(nameof(" + destination + "." + member + "), " + value.WithoutTrivia().ToFullString() + ")]");

            parameterNames = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "source" };
            var parameters = new List<string> { plan.SourceType.WithNullableAnnotation(NullableAnnotation.None).ToMinimalDisplayString(model, position) + " source" };
            foreach (var (member, type, _) in plan.Parameters)
            {
                var name = member.Name.Length == 0 ? "value" : char.ToLowerInvariant(member.Name[0]) + member.Name.Substring(1);
                while (!used.Add(name)) name += "Value";
                parameterNames.Add(name);
                var escaped = SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
                parameters.Add(type.ToMinimalDisplayString(model, position) + " " + escaped);
            }

            var text = string.Join("\n", attributes) + "\npublic static partial " + destination + " " + methodName + "(" + string.Join(", ", parameters) + ");\n";
            return ((MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(text)!).WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static InvocationExpressionSyntax Call(string mapper, string method, ExpressionSyntax source, IEnumerable<(string Name, ExpressionSyntax Value)> named)
        {
            var arguments = new List<ArgumentSyntax> { SyntaxFactory.Argument(source) };
            foreach (var (name, value) in named)
                arguments.Add(SyntaxFactory.Argument(value.WithoutTrivia()).WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(
                    SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name))));
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseExpression(mapper), SyntaxFactory.IdentifierName(method)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }

        /// <summary>Every implicit or explicit use of the operator, or null when one cannot be rewritten (a foreach element conversion, say).</summary>
        private static async Task<List<(Document, SyntaxNode, ExpressionSyntax)>?> FindOperatorUsesAsync(Solution solution, IMethodSymbol op, CancellationToken ct)
        {
            var result = new List<(Document, SyntaxNode, ExpressionSyntax)>();
            foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (root == null || model == null) continue;
                foreach (var node in root.DescendantNodes())
                {
                    if (node is ForEachStatementSyntax forEach &&
                        SymbolEqualityComparer.Default.Equals(model.GetForEachStatementInfo(forEach).ElementConversion.MethodSymbol, op))
                        return null;
                    if (!(node is ExpressionSyntax expression)) continue;
                    if (expression is CastExpressionSyntax cast)
                    {
                        if (model.GetOperation(cast, ct) is IConversionOperation castConversion &&
                            SymbolEqualityComparer.Default.Equals(castConversion.OperatorMethod, op))
                            result.Add((document, cast, cast.Expression));
                        continue;
                    }
                    if (expression.Parent is CastExpressionSyntax parentCast && parentCast.Expression == expression) continue;
                    var conversion = model.GetConversion(expression, ct);
                    // Assignments, arguments, returns: the conversion to the converted type of the expression.
                    if (conversion.IsUserDefined && SymbolEqualityComparer.Default.Equals(conversion.MethodSymbol, op))
                        result.Add((document, expression, expression));
                }
            }
            return result;
        }

        /// <summary>
        /// The mapper class name as a use site can write it. The generated method does not exist until the generator runs,
        /// so the simplifier cannot shorten the name; decide from the site's namespace and usings instead.
        /// </summary>
        private static string Qualify(SyntaxNode site, string? ns, string name)
        {
            if (ns == null) return name;
            var namespaces = site.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
            if (namespaces.Any(n => n == ns || n.StartsWith(ns + ".", StringComparison.Ordinal))) return name;
            var usings = site.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings)
                .Concat(site.SyntaxTree.GetCompilationUnitRoot().Usings)
                .Where(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None));
            return usings.Any(u => u.Name?.ToString() == ns) ? name : "global::" + ns + "." + name;
        }

        private static bool Imports(CompilationUnitSyntax root, SyntaxNode at) =>
            root.Usings.Any(u => u.Name?.ToString() == "ConventionMapper") ||
            at.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Usings.Any(u => u.Name?.ToString() == "ConventionMapper") ||
                                                                             n.Name.ToString() == "ConventionMapper" || n.Name.ToString().StartsWith("ConventionMapper.", StringComparison.Ordinal));

        private static bool Accessible(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) return Accessible(array.ElementType);
            for (var t = type as INamedTypeSymbol; t != null; t = t.ContainingType)
            {
                if (t.DeclaredAccessibility != Accessibility.Public) return false;
                if (t.TypeArguments.Any(a => !Accessible(a))) return false;
            }
            return true;
        }
    }
}
