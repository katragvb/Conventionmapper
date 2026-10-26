using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.CodeFixes
{
    /// <summary>
    /// "Generate build-time mapper": on a <c>CreateMap&lt;TSource, TDestination&gt;()</c> call, adds a new file with the
    /// equivalent <c>[GeneratedMapper]</c> class (ConventionMapper.SourceGenerator). Ignore, MapFrom member paths and
    /// ReverseMap are translated; anything else is listed at the top of the file to be converted by hand.
    /// </summary>
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(ConvertToGeneratedMapperRefactoring))]
    public sealed class ConvertToGeneratedMapperRefactoring : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || model == null) return;

            var createMap = FindCreateMap(root.FindNode(context.Span));
            if (createMap == null) return;
            if (!(model.GetSymbolInfo(createMap, context.CancellationToken).Symbol is IMethodSymbol method) ||
                method.ContainingType?.ContainingNamespace?.ToDisplayString() != "ConventionMapper" ||
                method.TypeArguments.Length != 2 ||
                !(method.TypeArguments[0] is INamedTypeSymbol source) || !(method.TypeArguments[1] is INamedTypeSymbol destination) ||
                source.IsGenericType && source.TypeArguments.Any(t => t is ITypeParameterSymbol) ||
                destination.IsGenericType && destination.TypeArguments.Any(t => t is ITypeParameterSymbol))
                return;

            var plan = MapPlan.Read(createMap);
            context.RegisterRefactoring(CodeAction.Create(
                "Generate build-time mapper for " + source.Name + " → " + destination.Name,
                ct => AddMapperAsync(context.Document, model, createMap, source, destination, plan, ct),
                nameof(ConvertToGeneratedMapperRefactoring)));
        }

        /// <summary>The CreateMap&lt;,&gt;() invocation at or around the cursor (including inside a chained call).</summary>
        internal static InvocationExpressionSyntax? FindCreateMap(SyntaxNode node)
        {
            for (var current = node; current != null; current = current.Parent)
            {
                if (current is InvocationExpressionSyntax invocation && IsCreateMap(invocation)) return invocation;
                if (current is StatementSyntax) break;
            }
            // Cursor elsewhere in a chained statement: look inside it.
            var statement = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
            return statement?.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(IsCreateMap);
        }

        private static bool IsCreateMap(InvocationExpressionSyntax invocation)
        {
            var name = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name : invocation.Expression as SimpleNameSyntax;
            return name is GenericNameSyntax generic && generic.Identifier.Text == "CreateMap" && generic.TypeArgumentList.Arguments.Count == 2;
        }

        private static Task<Solution> AddMapperAsync(Document document, SemanticModel model, InvocationExpressionSyntax createMap,
            INamedTypeSymbol source, INamedTypeSymbol destination, MapPlan plan, CancellationToken ct)
        {
            var compilation = model.Compilation;
            var containingType = model.GetEnclosingSymbol(createMap.SpanStart, ct)?.ContainingType;
            var ns = containingType?.ContainingNamespace is INamespaceSymbol n && !n.IsGlobalNamespace ? n.ToDisplayString() : null;

            var className = UniqueName(compilation, ns, source.Name + "Mapper", source.Name + "To" + destination.Name + "Mapper");
            var text = MapperSource(ns, className, source, destination, plan, compilation, containingType?.Name);
            var project = document.Project;
            var newDocument = project.AddDocument(className + ".cs", SourceText.From(text, Encoding.UTF8), document.Folders);
            return Task.FromResult(newDocument.Project.Solution);
        }

        private static string UniqueName(Compilation compilation, string? ns, params string[] candidates)
        {
            foreach (var candidate in candidates)
                if (compilation.GetTypeByMetadataName((ns == null ? string.Empty : ns + ".") + candidate) == null) return candidate;
            for (var i = 2; ; i++)
            {
                var name = candidates[candidates.Length - 1] + i;
                if (compilation.GetTypeByMetadataName((ns == null ? string.Empty : ns + ".") + name) == null) return name;
            }
        }

        internal static string MapperSource(string? ns, string className, INamedTypeSymbol source, INamedTypeSymbol destination, MapPlan plan,
            Compilation compilation, string? origin)
        {
            var usings = new SortedSet<string>(StringComparer.Ordinal) { "ConventionMapper" };
            foreach (var type in new[] { source, destination })
            {
                var typeNs = type.ContainingNamespace;
                if (typeNs != null && !typeNs.IsGlobalNamespace && typeNs.ToDisplayString() != ns) usings.Add(typeNs.ToDisplayString());
            }

            var sb = new StringBuilder();
            sb.Append("// Build-time mapper generated from CreateMap<").Append(source.Name).Append(", ").Append(destination.Name).Append(">()");
            if (origin != null) sb.Append(" in ").Append(origin);
            sb.AppendLine(".");
            sb.AppendLine("// Requires the ConventionMapper.SourceGenerator package. Once it builds, the CreateMap call can be removed.");
            if (compilation.GetTypeByMetadataName("ConventionMapper.GeneratedMapperAttribute") == null)
                sb.AppendLine("// ConventionMapper.SourceGenerator is not referenced yet: dotnet add package ConventionMapper.SourceGenerator");
            if (plan.Unconverted.Count > 0)
            {
                sb.AppendLine("//");
                sb.AppendLine("// Not converted automatically; convert by hand (for example with a static conversion method in this class):");
                foreach (var call in plan.Unconverted) sb.Append("//   ").AppendLine(call);
            }
            foreach (var u in usings) sb.Append("using ").Append(u).AppendLine(";");
            sb.AppendLine();

            var indent = string.Empty;
            if (ns != null)
            {
                sb.Append("namespace ").AppendLine(ns);
                sb.AppendLine("{");
                indent = "    ";
            }

            sb.Append(indent).AppendLine("[GeneratedMapper]");
            sb.Append(indent).Append("public static partial class ").AppendLine(className);
            sb.Append(indent).AppendLine("{");
            foreach (var ignored in plan.Ignored) sb.Append(indent).Append("    [MapIgnore(\"").Append(ignored).AppendLine("\")]");
            foreach (var pair in plan.MapFrom) sb.Append(indent).Append("    [MapMember(\"").Append(pair.Value).Append("\", \"").Append(pair.Key).AppendLine("\")]");
            var sourceType = source.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var destinationType = destination.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            sb.Append(indent).Append("    public static partial ").Append(destinationType).Append(" To").Append(destination.Name)
              .Append("(this ").Append(sourceType).AppendLine(" source);");
            if (plan.Reverse)
            {
                sb.AppendLine();
                sb.Append(indent).Append("    public static partial ").Append(sourceType).Append(" To").Append(source.Name)
                  .Append("(this ").Append(destinationType).AppendLine(" source);");
            }
            sb.Append(indent).AppendLine("}");
            if (ns != null) sb.AppendLine("}");
            return sb.ToString();
        }
    }

    /// <summary>What the chained configuration after CreateMap&lt;,&gt;() translates to.</summary>
    internal sealed class MapPlan
    {
        public List<string> Ignored { get; } = new List<string>();
        public List<KeyValuePair<string, string>> MapFrom { get; } = new List<KeyValuePair<string, string>>();
        public List<string> Unconverted { get; } = new List<string>();
        public bool Reverse { get; private set; }

        public static MapPlan Read(InvocationExpressionSyntax createMap)
        {
            var plan = new MapPlan();
            if (createMap.ArgumentList.Arguments.Count > 0) plan.Unconverted.Add("CreateMap(" + createMap.ArgumentList.Arguments + ")");

            SyntaxNode current = createMap;
            while (current.Parent is MemberAccessExpressionSyntax access && access.Expression == current && access.Parent is InvocationExpressionSyntax call)
            {
                var name = access.Name.Identifier.Text;
                if (plan.Reverse)
                    plan.Unconverted.Add("." + name + call.ArgumentList + "   (after ReverseMap)");
                else if (name == "ReverseMap" && call.ArgumentList.Arguments.Count == 0)
                    plan.Reverse = true;
                else if (name == "ForMember" && call.ArgumentList.Arguments.Count == 2 && plan.TryForMember(call))
                {
                }
                else
                    plan.Unconverted.Add("." + name + call.ArgumentList);
                current = call;
            }
            return plan;
        }

        /// <summary>ForMember(d =&gt; d.X, o =&gt; o.Ignore()) / o.MapFrom(s =&gt; s.A.B) / o.MapFrom("A.B").</summary>
        private bool TryForMember(InvocationExpressionSyntax call)
        {
            var destination = MemberName(call.ArgumentList.Arguments[0].Expression);
            if (destination == null || !(call.ArgumentList.Arguments[1].Expression is LambdaExpressionSyntax options) ||
                !(options.Body is InvocationExpressionSyntax option) || !(option.Expression is MemberAccessExpressionSyntax optionAccess))
                return false;

            switch (optionAccess.Name.Identifier.Text)
            {
                case "Ignore" when option.ArgumentList.Arguments.Count == 0:
                    Ignored.Add(destination);
                    return true;
                case "MapFrom" when option.ArgumentList.Arguments.Count == 1:
                    var argument = option.ArgumentList.Arguments[0].Expression;
                    var path = argument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                        ? literal.Token.ValueText
                        : MemberPath(argument);
                    if (path == null) return false;
                    MapFrom.Add(new KeyValuePair<string, string>(destination, path));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>d =&gt; d.Name gives "Name" (single member only).</summary>
        private static string? MemberName(ExpressionSyntax expression)
        {
            var path = MemberPath(expression);
            return path != null && path.IndexOf('.') < 0 ? path : null;
        }

        /// <summary>s =&gt; s.Customer.Address.City gives "Customer.Address.City".</summary>
        private static string? MemberPath(ExpressionSyntax expression)
        {
            if (!(expression is SimpleLambdaExpressionSyntax lambda) || !(lambda.Body is ExpressionSyntax body)) return null;
            var parameter = lambda.Parameter.Identifier.Text;
            var names = new List<string>();
            while (body is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                names.Insert(0, access.Name.Identifier.Text);
                body = access.Expression;
            }
            return body is IdentifierNameSyntax root && root.Identifier.Text == parameter && names.Count > 0 ? string.Join(".", names) : null;
        }
    }
}
