// Shared by ConventionMapper.SourceGenerator and ConventionMapper.Analyzers (linked into both projects).
// Mirrors the runtime naming conventions: case-insensitive names, Get-methods and flattening.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ConventionMapper.Roslyn
{
    internal sealed class MemberResolver
    {
        private const int MaxFlattenDepth = 5;

        private readonly Func<ISymbol, bool> _isAccessible;
        private readonly Dictionary<ITypeSymbol, List<ISymbol>> _readable = new Dictionary<ITypeSymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

        public MemberResolver(Func<ISymbol, bool> isAccessible)
        {
            _isAccessible = isAccessible;
        }

        public static ITypeSymbol TypeOf(ISymbol member)
        {
            switch (member)
            {
                case IPropertySymbol p: return p.Type;
                case IFieldSymbol f: return f.Type;
                case IMethodSymbol m: return m.ReturnType;
                case IParameterSymbol a: return a.Type;
                default: throw new ArgumentException("Unsupported member " + member, nameof(member));
            }
        }

        /// <summary>True when the member carries ConventionMapper's [Sensitive] attribute (from either package), here or on an overridden base member.</summary>
        public static bool IsSensitive(ISymbol member)
        {
            for (var current = member; current != null; current = (current as IPropertySymbol)?.OverriddenProperty)
                foreach (var attribute in current.GetAttributes())
                    if (attribute.AttributeClass?.ToDisplayString() == "ConventionMapper.SensitiveAttribute") return true;
            return false;
        }

        /// <summary>Name used for convention matching: GetTotal() matches "Total".</summary>
        public static string ConventionName(ISymbol member) =>
            member is IMethodSymbol ? member.Name.Substring(3) : member.Name;

        public static bool IsNullableValueType(ITypeSymbol type, out ITypeSymbol underlying)
        {
            if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                underlying = named.TypeArguments[0];
                return true;
            }
            underlying = type;
            return false;
        }

        /// <summary>Readable instance properties, fields and parameterless Get-methods, most derived first.</summary>
        public IReadOnlyList<ISymbol> GetReadableMembers(ITypeSymbol type)
        {
            if (_readable.TryGetValue(type, out var cached)) return cached;

            var result = new List<ISymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in SelfAndBaseTypes(type))
            {
                foreach (var member in t.GetMembers())
                {
                    if (member.IsStatic || !member.CanBeReferencedByName) continue;
                    var ok = false;
                    switch (member)
                    {
                        case IPropertySymbol p:
                            ok = !p.IsIndexer && p.GetMethod != null && _isAccessible(p.GetMethod);
                            break;
                        case IFieldSymbol f:
                            ok = _isAccessible(f);
                            break;
                        case IMethodSymbol m:
                            ok = m.MethodKind == MethodKind.Ordinary && m.Parameters.Length == 0 && !m.ReturnsVoid && !m.IsGenericMethod &&
                                 m.Name.Length > 3 && m.Name.StartsWith("Get", StringComparison.Ordinal) && _isAccessible(m);
                            break;
                    }
                    if (ok && names.Add(member.Name)) result.Add(member);
                }
            }
            _readable[type] = result;
            return result;
        }

        private static IEnumerable<ITypeSymbol> SelfAndBaseTypes(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                yield return type;
                foreach (var i in type.AllInterfaces) yield return i;
                yield break;
            }
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
                yield return t;
        }

        /// <summary>Finds the source member chain for a destination name: exact (case-insensitive) name, Get-method, then flattening.</summary>
        public List<ISymbol>? FindPath(ITypeSymbol sourceType, string destinationName) => FindPath(sourceType, destinationName, 0);

        private List<ISymbol>? FindPath(ITypeSymbol sourceType, string name, int depth)
        {
            if (depth > MaxFlattenDepth || name.Length == 0) return null;
            if (IsNullableValueType(sourceType, out _)) return null;
            if (sourceType.SpecialType != SpecialType.None || sourceType.TypeKind == TypeKind.Enum) return null;

            var members = GetReadableMembers(sourceType);
            // Properties and fields win over Get-methods with the same convention name.
            foreach (var member in members)
                if (!(member is IMethodSymbol) && string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                    return new List<ISymbol> { member };
            foreach (var member in members)
                if (member is IMethodSymbol && string.Equals(ConventionName(member), name, StringComparison.OrdinalIgnoreCase))
                    return new List<ISymbol> { member };

            // Flattening: CustomerAddressCity -> Customer.Address.City (longest matching prefix first).
            foreach (var member in members.OrderByDescending(m => ConventionName(m).Length))
            {
                var prefix = ConventionName(member);
                if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var memberType = TypeOf(member);
                if (memberType.IsValueType) continue;
                var rest = FindPath(memberType, name.Substring(prefix.Length), depth + 1);
                if (rest != null)
                {
                    rest.Insert(0, member);
                    return rest;
                }
            }
            return null;
        }

        /// <summary>Resolves an explicit dotted path such as "Customer.Address.City" (case-insensitive segments).</summary>
        public List<ISymbol>? FindExplicitPath(ITypeSymbol sourceType, string path)
        {
            var result = new List<ISymbol>();
            var current = sourceType;
            foreach (var segment in path.Split('.'))
            {
                var name = segment.Trim();
                if (name.EndsWith("()", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
                var members = GetReadableMembers(current);
                var match = members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;
                result.Add(match);
                current = TypeOf(match);
            }
            return result.Count == 0 ? null : result;
        }
    }
}
