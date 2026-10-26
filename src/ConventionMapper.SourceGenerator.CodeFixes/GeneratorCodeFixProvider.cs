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

namespace ConventionMapper.SourceGenerator.CodeFixes
{
    /// <summary>
    /// CM0001 (unmapped member): "Ignore 'X'", "Ignore all N unmapped members", "Map only the mapped members ([MapExplicit])",
    /// and in explicit mode "Remove 'X' from [MapExplicit]".
    /// CM0020 (member name as a string): "Use nameof(Type.Member)", fixable across a document, project or solution.
    /// CM0026 (member looks like personal data): "Mark [Sensitive]".
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GeneratorCodeFixProvider))]
    public sealed class GeneratorCodeFixProvider : CodeFixProvider
    {
        private const string UnmappedMemberId = "CM0001";
        private const string UseNameofId = "CM0020";
        private const string SuggestSensitiveId = "CM0026";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UnmappedMemberId, UseNameofId, SuggestSensitiveId);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var unmapped = new List<(Diagnostic Diagnostic, string Member)>();
            MethodDeclarationSyntax? unmappedMethod = null;
            foreach (var diagnostic in context.Diagnostics)
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                if (diagnostic.Id == SuggestSensitiveId)
                {
                    var declaration = (MemberDeclarationSyntax?)node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() ?? node.FirstAncestorOrSelf<FieldDeclarationSyntax>();
                    if (declaration == null) continue;
                    context.RegisterCodeFix(
                        CodeAction.Create("Mark [Sensitive]", ct => MarkSensitiveAsync(context.Document, declaration, ct), SuggestSensitiveId),
                        diagnostic);
                    continue;
                }
                if (diagnostic.Id == UseNameofId &&
                    diagnostic.Properties.TryGetValue("Type", out var type) && type != null &&
                    diagnostic.Properties.TryGetValue("Member", out var nameofMember) && nameofMember != null &&
                    node.FirstAncestorOrSelf<LiteralExpressionSyntax>() is LiteralExpressionSyntax literal)
                {
                    var shortType = type.StartsWith("global::", StringComparison.Ordinal) ? type.Substring(8) : type;
                    context.RegisterCodeFix(
                        CodeAction.Create("Use nameof(" + shortType.Split('.').Last() + "." + nameofMember + ")",
                            ct => UseNameofAsync(context.Document, literal, type, nameofMember, ct),
                            UseNameofId),
                        diagnostic);
                }
                else if (diagnostic.Id == UnmappedMemberId &&
                         diagnostic.Properties.TryGetValue("Member", out var member) && member != null &&
                         node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is MethodDeclarationSyntax method)
                {
                    unmappedMethod = method;
                    unmapped.Add((diagnostic, member));
                    context.RegisterCodeFix(
                        CodeAction.Create("Ignore '" + member + "'",
                            ct => ChangeMethodAsync(context.Document, method, m => SyntaxHelpers.AddAttribute(m, SyntaxHelpers.Attribute("MapIgnore", member)), ct),
                            UnmappedMemberId + ":ignore:" + member),
                        diagnostic);

                    var explicitMode = diagnostic.Properties.TryGetValue("ExplicitMode", out var mode) && mode == "true";
                    if (explicitMode && FindListedArgument(method, member) != null)
                    {
                        context.RegisterCodeFix(
                            CodeAction.Create("Remove '" + member + "' from [MapExplicit]",
                                ct => ChangeMethodAsync(context.Document, method, m => RemoveListed(m, member), ct),
                                UnmappedMemberId + ":unlist:" + member),
                            diagnostic);
                    }
                    else if (!explicitMode && diagnostic.Properties.TryGetValue("Mapped", out var mappedList) && !string.IsNullOrEmpty(mappedList))
                    {
                        var mapped = mappedList!.Split(',');
                        context.RegisterCodeFix(
                            CodeAction.Create("Map only the " + mapped.Length + " mapped member(s) ([MapExplicit])",
                                ct => ChangeMethodAsync(context.Document, method, m => SyntaxHelpers.AddAttribute(m, SyntaxHelpers.Attribute("MapExplicit", mapped)), ct),
                                UnmappedMemberId + ":explicit"),
                            diagnostic);
                    }
                }
            }

            var members = unmapped.Select(u => u.Member).Distinct(StringComparer.Ordinal).ToArray();
            if (unmappedMethod != null && members.Length > 1)
            {
                var target = unmappedMethod;
                context.RegisterCodeFix(
                    CodeAction.Create("Ignore all " + members.Length + " unmapped members",
                        ct => ChangeMethodAsync(context.Document, target, m =>
                        {
                            foreach (var name in members) m = SyntaxHelpers.AddAttribute(m, SyntaxHelpers.Attribute("MapIgnore", name));
                            return m;
                        }, ct),
                        UnmappedMemberId + ":ignore-all"),
                    unmapped.Select(u => u.Diagnostic).ToImmutableArray());
            }
        }

        private static async Task<Document> UseNameofAsync(Document document, LiteralExpressionSyntax literal, string type, string member, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (root == null) return document;

            // The simplifier does not shorten names inside nameof, so use the shortest name that binds at this position.
            var typeName = type;
            var method = literal.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (model != null && method != null && model.GetDeclaredSymbol(method, ct) is IMethodSymbol symbol && Destination(symbol) is ITypeSymbol destination)
                typeName = destination.ToMinimalDisplayString(model, literal.SpanStart);
            var nameof = SyntaxFactory.ParseExpression("nameof(" + typeName + "." + member + ")").WithTriviaFrom(literal);
            return document.WithSyntaxRoot(root.ReplaceNode(literal, nameof));
        }

        /// <summary>The destination type of a create, update or projection method (as the generator sees it).</summary>
        private static ITypeSymbol? Destination(IMethodSymbol method)
        {
            if (method.ReturnType is INamedTypeSymbol returned && returned.IsGenericType)
            {
                var definition = returned.OriginalDefinition.ToDisplayString();
                if (definition == "System.Linq.IQueryable<T>") return returned.TypeArguments[0];
                if (definition == "System.Linq.Expressions.Expression<TDelegate>" && returned.TypeArguments[0] is INamedTypeSymbol func && func.TypeArguments.Length == 2)
                    return func.TypeArguments[1];
            }
            if (!method.ReturnsVoid) return method.ReturnType;
            return method.Parameters.Length >= 2 ? method.Parameters[1].Type : null;
        }

        private static async Task<Document> MarkSensitiveAsync(Document document, MemberDeclarationSyntax declaration, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) as CompilationUnitSyntax;
            if (root == null) return document;
            var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("Sensitive"))));
            MemberDeclarationSyntax updated;
            if (declaration.AttributeLists.Count > 0)
            {
                updated = declaration.WithAttributeLists(new SyntaxList<AttributeListSyntax>(new[] { list.WithLeadingTrivia(declaration.AttributeLists[0].GetLeadingTrivia()).WithTrailingTrivia(SyntaxFactory.Space) }
                    .Concat(declaration.AttributeLists.Select((a, i) => i == 0 ? a.WithLeadingTrivia() : a))));
            }
            else
            {
                // On the same line, before the modifiers: "[Sensitive] public string Ssn { get; set; }".
                updated = declaration.WithoutLeadingTrivia()
                    .WithAttributeLists(SyntaxFactory.SingletonList(list.WithLeadingTrivia(declaration.GetLeadingTrivia()).WithTrailingTrivia(SyntaxFactory.Space)));
            }
            var newRoot = root.ReplaceNode(declaration, updated);
            if (!newRoot.Usings.Any(u => u.Name?.ToString() == "ConventionMapper") &&
                !declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.ToString() == "ConventionMapper" || n.Name.ToString().StartsWith("ConventionMapper.", StringComparison.Ordinal)))
                newRoot = newRoot.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("ConventionMapper")).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")));
            return document.WithSyntaxRoot(newRoot);
        }

        private static async Task<Document> ChangeMethodAsync(Document document, MethodDeclarationSyntax method, Func<MethodDeclarationSyntax, MethodDeclarationSyntax> change, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) return document;
            return document.WithSyntaxRoot(root.ReplaceNode(method, change(method)));
        }

        private static AttributeArgumentSyntax? FindListedArgument(MethodDeclarationSyntax method, string member) =>
            method.AttributeLists.SelectMany(l => l.Attributes)
                .Where(a => SyntaxHelpers.IsAttribute(a, "MapExplicit") && a.ArgumentList != null)
                .SelectMany(a => a.ArgumentList!.Arguments)
                .FirstOrDefault(arg => string.Equals(SyntaxHelpers.ArgumentName(arg), member, StringComparison.OrdinalIgnoreCase));

        private static MethodDeclarationSyntax RemoveListed(MethodDeclarationSyntax method, string member)
        {
            var argument = FindListedArgument(method, member);
            if (argument == null || !(argument.Parent is AttributeArgumentListSyntax list)) return method;
            var remaining = list.Arguments.Remove(argument);
            SyntaxNode replacement = remaining.Count == 0
                ? (SyntaxNode)((AttributeSyntax)list.Parent!).WithArgumentList(null)
                : list.WithArguments(remaining);
            return method.ReplaceNode(remaining.Count == 0 ? list.Parent! : list, replacement);
        }
    }
}
