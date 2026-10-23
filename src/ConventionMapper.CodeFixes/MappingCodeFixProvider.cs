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
using Microsoft.CodeAnalysis.Simplification;

namespace ConventionMapper.CodeFixes
{
    /// <summary>
    /// CM1001: appends <c>.ForMember(d =&gt; d.X, o =&gt; o.Ignore())</c> for unmapped members.
    /// CM1002: rewrites <c>Map&lt;TDestination&gt;(x)</c> to <c>Map&lt;TSource, TDestination&gt;(x)</c>.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MappingCodeFixProvider))]
    public sealed class MappingCodeFixProvider : CodeFixProvider
    {
        private const string UnmappedMemberId = "CM1001";
        private const string PreferTypedMapId = "CM1002";

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(UnmappedMemberId, PreferTypedMapId);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var unmapped = new List<string>();
            InvocationExpressionSyntax? createMap = null;
            foreach (var diagnostic in context.Diagnostics)
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                if (diagnostic.Id == UnmappedMemberId && diagnostic.Properties.TryGetValue("Member", out var member) && member != null)
                {
                    var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    if (invocation == null) continue;
                    createMap = invocation;
                    unmapped.Add(member);
                    context.RegisterCodeFix(
                        CodeAction.Create("Ignore '" + member + "'",
                            ct => IgnoreMembersAsync(context.Document, invocation, new[] { member }, ct),
                            UnmappedMemberId + ":" + member),
                        diagnostic);
                }
                else if (diagnostic.Id == PreferTypedMapId && diagnostic.Properties.TryGetValue("SourceType", out var sourceType) && sourceType != null)
                {
                    var generic = node.FirstAncestorOrSelf<GenericNameSyntax>();
                    if (generic == null || generic.TypeArgumentList.Arguments.Count != 1) continue;
                    context.RegisterCodeFix(
                        CodeAction.Create("Use Map<TSource, TDestination>",
                            ct => UseTypedMapAsync(context.Document, generic, sourceType, ct),
                            PreferTypedMapId),
                        diagnostic);
                }
            }

            if (createMap != null && unmapped.Count > 1)
            {
                var members = unmapped.Distinct().ToArray();
                var target = createMap;
                context.RegisterCodeFix(
                    CodeAction.Create("Ignore all " + members.Length + " unmapped members",
                        ct => IgnoreMembersAsync(context.Document, target, members, ct),
                        UnmappedMemberId + ":all"),
                    context.Diagnostics.Where(d => d.Id == UnmappedMemberId).ToImmutableArray());
            }
        }

        private static async Task<Document> IgnoreMembersAsync(Document document, InvocationExpressionSyntax invocation, IEnumerable<string> members, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) return document;

            ExpressionSyntax replacement = invocation.WithoutTrailingTrivia();
            foreach (var member in members)
            {
                replacement = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, replacement, SyntaxFactory.IdentifierName("ForMember")),
                    SyntaxFactory.ParseArgumentList("(d => d." + member + ", o => o.Ignore())"));
            }
            replacement = replacement.WithTrailingTrivia(invocation.GetTrailingTrivia());
            return document.WithSyntaxRoot(root.ReplaceNode(invocation, replacement));
        }

        private static async Task<Document> UseTypedMapAsync(Document document, GenericNameSyntax generic, string sourceType, CancellationToken ct)
        {
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) return document;

            var source = SyntaxFactory.ParseTypeName(sourceType).WithAdditionalAnnotations(Simplifier.Annotation);
            var arguments = SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(new[] { source, generic.TypeArgumentList.Arguments[0] }));
            return document.WithSyntaxRoot(root.ReplaceNode(generic, generic.WithTypeArgumentList(arguments)));
        }
    }
}
