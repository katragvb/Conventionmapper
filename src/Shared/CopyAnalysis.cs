using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>
    /// A hand-written copy, <c>new D { A = s.A, B = s.B, C = s.Other.C, D = 1, E = Compute(s) }</c>, split into what a
    /// generated mapping method can express: same-name members (convention, listed in [MapExplicit]), other members or
    /// paths of the same source ([MapMember]), constants ([MapConstant]), and everything else, which becomes a method
    /// parameter so the expression stays at the call site unchanged. Shared by the CM0029 analyzer and its fix.
    /// </summary>
    internal sealed class CopyPlan
    {
        public CopyPlan(BaseObjectCreationExpressionSyntax creation, INamedTypeSymbol destination, ISymbol source, ITypeSymbol sourceType)
        {
            Creation = creation;
            Destination = destination;
            Source = source;
            SourceType = sourceType;
        }

        public BaseObjectCreationExpressionSyntax Creation { get; }
        public INamedTypeSymbol Destination { get; }

        /// <summary>The local or parameter the members are read from.</summary>
        public ISymbol Source { get; }
        public ITypeSymbol SourceType { get; }

        /// <summary>Destination members copied from the source member of the same name, in initializer order.</summary>
        public List<string> Convention { get; } = new List<string>();

        /// <summary>(source path, destination member) for members copied from another member or a dotted path.</summary>
        public List<(string Path, string Destination)> Renamed { get; } = new List<(string, string)>();

        /// <summary>(destination member, constant expression) for constants that are valid attribute arguments.</summary>
        public List<(string Destination, ExpressionSyntax Value)> Constants { get; } = new List<(string, ExpressionSyntax)>();

        /// <summary>Everything else: the member and the value expression, which becomes an argument at the call site.</summary>
        public List<(ISymbol Member, ITypeSymbol Type, ExpressionSyntax Value)> Parameters { get; } = new List<(ISymbol, ITypeSymbol, ExpressionSyntax)>();

        /// <summary>The conversion operator whose whole body is this copy of its parameter, if any.</summary>
        public IMethodSymbol? Operator { get; set; }
        public ConversionOperatorDeclarationSyntax? OperatorDeclaration { get; set; }
    }

    internal static class CopyAnalysis
    {
        public const string MinimumMembersOption = "conventionmapper.copy_min_members";
        public const int DefaultMinimumMembers = 3;

        public static CopyPlan? Analyze(BaseObjectCreationExpressionSyntax creation, SemanticModel model, CancellationToken ct)
        {
            if (creation.Initializer == null || !creation.Initializer.IsKind(SyntaxKind.ObjectInitializerExpression)) return null;
            if (creation.ArgumentList != null && creation.ArgumentList.Arguments.Count > 0) return null;
            if (!(model.GetTypeInfo(creation, ct).Type is INamedTypeSymbol destination) || destination.IsAnonymousType ||
                destination.TypeKind != TypeKind.Class && destination.TypeKind != TypeKind.Struct) return null;
            if (InExpressionTree(creation, model, ct)) return null;

            // Every initializer entry must be "Member = value"; nested object or collection initializers stay hand-written.
            var entries = new List<(ISymbol Member, ITypeSymbol Type, ExpressionSyntax Value)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expression in creation.Initializer.Expressions)
            {
                if (!(expression is AssignmentExpressionSyntax assignment) || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                    !(assignment.Left is IdentifierNameSyntax) || assignment.Right is InitializerExpressionSyntax) return null;
                var member = model.GetSymbolInfo(assignment.Left, ct).Symbol;
                ITypeSymbol? type = member is IPropertySymbol p ? p.Type : (member as IFieldSymbol)?.Type;
                if (type == null || member!.IsStatic || !seen.Add(member.Name)) return null;
                entries.Add((member, type, assignment.Right));
            }

            // The source is the local or parameter most members are read from.
            var roots = entries.Select(e => (Entry: e, Path: SourcePath(e.Value, model, ct))).ToList();
            var source = roots.Where(r => r.Path != null).GroupBy(r => r.Path!.Value.Root, SymbolEqualityComparer.Default)
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (source == null) return null;
            var sourceType = source is ILocalSymbol local ? local.Type : ((IParameterSymbol)source).Type;
            if (sourceType.TypeKind == TypeKind.TypeParameter || sourceType.IsAnonymousType) return null;

            var plan = new CopyPlan(creation, destination, source, sourceType);
            foreach (var (entry, path) in roots)
            {
                if (path != null && SymbolEqualityComparer.Default.Equals(path.Value.Root, source) && Copies(entry.Value, entry.Type, model, ct))
                {
                    if (path.Value.Members == entry.Member.Name) plan.Convention.Add(entry.Member.Name);
                    else plan.Renamed.Add((path.Value.Members, entry.Member.Name));
                }
                else if (IsAttributeConstant(entry.Value, entry.Type, model, ct)) plan.Constants.Add((entry.Member.Name, entry.Value));
                else plan.Parameters.Add(entry);
            }

            // "implicit operator D(S source) => new D { ... }" (or a body that only returns it).
            var parent = creation.Parent;
            if (parent is ReturnStatementSyntax ret && ret.Parent is BlockSyntax block && block.Statements.Count == 1) parent = block;
            if ((parent is ArrowExpressionClauseSyntax || parent is BlockSyntax) && parent.Parent is ConversionOperatorDeclarationSyntax op &&
                model.GetDeclaredSymbol(op, ct) is IMethodSymbol conversion && conversion.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(conversion.Parameters[0], source))
            {
                plan.Operator = conversion;
                plan.OperatorDeclaration = op;
            }
            return plan;
        }

        /// <summary>"s.A.B" → (s, "A.B"): instance properties and fields only, no calls, no null-conditional access.</summary>
        private static (ISymbol Root, string Members)? SourcePath(ExpressionSyntax value, SemanticModel model, CancellationToken ct)
        {
            var names = new List<string>();
            var current = value;
            while (current is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                var symbol = model.GetSymbolInfo(access, ct).Symbol;
                if (!(symbol is IPropertySymbol property && !property.IsIndexer || symbol is IFieldSymbol) || symbol!.IsStatic) return null;
                names.Insert(0, access.Name.Identifier.ValueText);
                current = access.Expression;
            }
            if (names.Count == 0 || !(current is IdentifierNameSyntax root)) return null;
            var rootSymbol = model.GetSymbolInfo(root, ct).Symbol;
            if (!(rootSymbol is ILocalSymbol || rootSymbol is IParameterSymbol)) return null;
            return (rootSymbol!, string.Join(".", names));
        }

        /// <summary>
        /// The generator copies the value as written: the same type (nullability aside), or a built-in implicit numeric
        /// or nullable conversion. User-defined conversions stay at the call site, where they keep running as before.
        /// </summary>
        private static bool Copies(ExpressionSyntax value, ITypeSymbol destination, SemanticModel model, CancellationToken ct)
        {
            var type = model.GetTypeInfo(value, ct).Type;
            if (type == null) return false;
            if (SymbolEqualityComparer.Default.Equals(type, destination)) return true;
            var conversion = model.ClassifyConversion(value, destination);
            return conversion.IsImplicit && !conversion.IsUserDefined && (conversion.IsNumeric || conversion.IsNullable);
        }

        /// <summary>A constant of the destination's type that C# accepts as an attribute argument (not decimal, not null).</summary>
        private static bool IsAttributeConstant(ExpressionSyntax value, ITypeSymbol destination, SemanticModel model, CancellationToken ct)
        {
            var constant = model.GetConstantValue(value, ct);
            if (!constant.HasValue || constant.Value == null) return false;
            var type = model.GetTypeInfo(value, ct).Type;
            if (type == null || !SymbolEqualityComparer.Default.Equals(type, destination)) return false;
            var underlying = type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum ? named.EnumUnderlyingType : type;
            return underlying != null && underlying.SpecialType != SpecialType.System_Decimal &&
                   (underlying.SpecialType == SpecialType.System_String || underlying.SpecialType == SpecialType.System_Boolean ||
                    underlying.SpecialType == SpecialType.System_Char ||
                    underlying.SpecialType >= SpecialType.System_SByte && underlying.SpecialType <= SpecialType.System_Double);
        }

        /// <summary>Inside a lambda that becomes an expression tree (an EF query): a method call there would not translate.</summary>
        private static bool InExpressionTree(SyntaxNode node, SemanticModel model, CancellationToken ct)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is AnonymousFunctionExpressionSyntax lambda &&
                    model.GetTypeInfo(lambda, ct).ConvertedType is INamedTypeSymbol converted &&
                    converted.Name == "Expression" && converted.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions")
                    return true;
                if (ancestor is QueryExpressionSyntax query && model.GetTypeInfo(query, ct).Type is ITypeSymbol queryType &&
                    (queryType.Name == "IQueryable" || queryType.AllInterfaces.Any(i => i.Name == "IQueryable")))
                    return true;
                if (ancestor is MemberDeclarationSyntax) break;
            }
            return false;
        }
    }
}
