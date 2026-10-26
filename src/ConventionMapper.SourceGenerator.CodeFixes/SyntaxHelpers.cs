using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConventionMapper.SourceGenerator.CodeFixes
{
    internal static class SyntaxHelpers
    {
        /// <summary>Adds an attribute list on its own line, keeping doc comments above the attributes.</summary>
        public static MethodDeclarationSyntax AddAttribute(MethodDeclarationSyntax method, AttributeSyntax attribute)
        {
            var indentation = Indentation(method);
            var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
            if (method.AttributeLists.Count > 0)
            {
                var last = method.AttributeLists.Last();
                list = list.WithLeadingTrivia(SyntaxFactory.Whitespace(indentation)).WithTrailingTrivia(last.GetTrailingTrivia());
                return method.WithAttributeLists(method.AttributeLists.Add(list));
            }

            // The doc comment and indentation sit on the first modifier; move them in front of the new attribute.
            var leading = method.GetLeadingTrivia();
            list = list.WithLeadingTrivia(leading).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
            var stripped = method.WithLeadingTrivia(SyntaxFactory.Whitespace(indentation));
            return stripped.WithAttributeLists(SyntaxFactory.SingletonList(list));
        }

        /// <summary>The whitespace after the last line break before the method (its indentation).</summary>
        public static string Indentation(SyntaxNode node)
        {
            var trivia = node.GetLeadingTrivia();
            var result = string.Empty;
            foreach (var t in trivia)
            {
                if (t.IsKind(SyntaxKind.EndOfLineTrivia)) result = string.Empty;
                else if (t.IsKind(SyntaxKind.WhitespaceTrivia)) result = t.ToString();
                else if (t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)) result = string.Empty; // ends with a line break
            }
            return result;
        }

        public static AttributeSyntax Attribute(string name, params string[] stringArguments)
        {
            var attribute = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName(name));
            if (stringArguments.Length == 0) return attribute;
            return attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(
                stringArguments.Select(a => SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(a)))))));
        }

        /// <summary>"MapExplicit" for [MapExplicit], [MapExplicitAttribute] and [ConventionMapper.MapExplicit].</summary>
        public static bool IsAttribute(AttributeSyntax attribute, string shortName)
        {
            var name = attribute.Name switch
            {
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => attribute.Name.ToString(),
            };
            return name == shortName || name == shortName + "Attribute";
        }

        /// <summary>The member name an attribute argument stands for: "X" or nameof(T.X).</summary>
        public static string? ArgumentName(AttributeArgumentSyntax argument)
        {
            switch (argument.Expression)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return literal.Token.ValueText;
                case InvocationExpressionSyntax invocation when invocation.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "nameof" &&
                                                                invocation.ArgumentList.Arguments.Count == 1:
                    var expression = invocation.ArgumentList.Arguments[0].Expression;
                    return expression is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : expression.ToString();
                default:
                    return null;
            }
        }
    }
}
