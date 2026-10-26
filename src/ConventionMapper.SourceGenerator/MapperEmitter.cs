using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using ConventionMapper.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>Generates the implementation of every unimplemented partial method of one [GeneratedMapper] class.</summary>
    internal sealed class MapperEmitter
    {
        /// <summary>Signatures keep nullable annotations so they match the user's partial declarations.</summary>
        private static readonly SymbolDisplayFormat SignatureFormat =
            SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <summary>Types in expressions (new, casts, typeof) and cache keys: no nullable reference modifier.</summary>
        private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        private enum CollectionKind { None, Array, List, HashSet, Dictionary }

        private readonly INamedTypeSymbol _mapper;
        private readonly CSharpCompilation _compilation;
        private readonly CancellationToken _ct;
        private readonly MemberResolver _resolver;
        private readonly INamedTypeSymbol _nullableT;
        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private readonly StringBuilder _helpers = new StringBuilder();
        private readonly Dictionary<string, string> _helperNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, IMethodSymbol> _userMaps = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);

        /// <summary>Ordinary (non-partial) static conversion methods; the only user methods used inside projections.</summary>
        private readonly Dictionary<string, IMethodSymbol> _conversionMethods = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedEnumPairs = new HashSet<string>(StringComparer.Ordinal);

        // Data-flow report: "`S` → `D`" -> destination member -> table row; sensitive member -> destinations.
        private readonly SortedDictionary<string, SortedDictionary<string, string>> _flows = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, SortedSet<string>> _sensitive = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        private int _counter;
        private int _memberIndent;
        private Location _location = Location.None;
        private IMethodSymbol? _current;
        private bool _atRoot;

        private MapperEmitter(INamedTypeSymbol mapper, CSharpCompilation compilation, CancellationToken ct)
        {
            _mapper = mapper;
            _compilation = compilation;
            _ct = ct;
            _resolver = new MemberResolver(IsAccessible);
            _nullableT = compilation.GetSpecialType(SpecialType.System_Nullable_T);
        }

        public static MapperResult Run(INamedTypeSymbol mapper, TypeDeclarationSyntax node, Compilation compilation, CancellationToken ct)
        {
            var hintName = HintName(mapper);
            if (!(compilation is CSharpCompilation csharp)) return new MapperResult(hintName, null, ImmutableArray<Diagnostic>.Empty);
            return new MapperEmitter(mapper, csharp, ct).Emit(node, hintName);
        }

        private bool IsAccessible(ISymbol symbol) => _compilation.IsSymbolAccessibleWithin(symbol, _mapper);

        // ================================================================== file layout

        private MapperResult Emit(TypeDeclarationSyntax node, string hintName)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var t = _mapper; t != null; t = t.ContainingType) chain.Insert(0, t);

            foreach (var type in chain)
            {
                foreach (var reference in type.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(_ct) is TypeDeclarationSyntax declaration && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                    {
                        _diagnostics.Add(Diagnostic.Create(Diagnostics.NotPartial, node.Identifier.GetLocation(), _mapper.Name));
                        return new MapperResult(hintName, null, _diagnostics.ToImmutableArray());
                    }
                }
            }

            var methods = new List<KeyValuePair<IMethodSymbol, MethodDeclarationSyntax>>();
            foreach (var member in _mapper.GetMembers())
            {
                if (!(member is IMethodSymbol method) || method.MethodKind != MethodKind.Ordinary || method.PartialImplementationPart != null) continue;
                foreach (var reference in method.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(_ct) is MethodDeclarationSyntax syntax && syntax.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                        syntax.Body == null && syntax.ExpressionBody == null)
                    {
                        methods.Add(new KeyValuePair<IMethodSymbol, MethodDeclarationSyntax>(method, syntax));
                        break;
                    }
                }
            }

            // Static methods of the mapper that convert one type to another are reused wherever that pair occurs:
            // the partial mapping methods themselves, and ordinary helpers such as "static string Format(Money m)".
            // Ordinary methods win over partial ones for the same pair, because they express a deliberate choice.
            foreach (var pair in methods) RegisterUserMap(pair.Key);
            foreach (var member in _mapper.GetMembers())
            {
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary && !IsPartialDefinition(method))
                    RegisterUserMap(method, overwrite: true);
            }

            var hasNamespace = !_mapper.ContainingNamespace.IsGlobalNamespace;
            _memberIndent = (hasNamespace ? 1 : 0) + chain.Count;

            var body = new CodeWriter(_memberIndent);
            var first = true;
            foreach (var pair in methods)
            {
                _ct.ThrowIfCancellationRequested();
                if (!first) body.Line();
                first = false;
                EmitMethod(pair.Key, pair.Value, body);
            }

            var file = new CodeWriter(0);
            file.Line("// <auto-generated/>");
            file.Line("// Generated by ConventionMapper.SourceGenerator. Do not edit.");
            file.Line("#nullable enable");
            file.Line("#pragma warning disable");
            file.Line();
            if (hasNamespace) file.Open("namespace " + _mapper.ContainingNamespace.ToDisplayString());
            foreach (var type in chain) file.Open("partial " + TypeKeyword(type) + " " + type.Name + TypeParameterList(type));
            file.Raw(body.ToString());
            file.Raw(_helpers.ToString());
            foreach (var _ in chain) file.Close();
            if (hasNamespace) file.Close();

            return new MapperResult(hintName, file.ToString(), _diagnostics.ToImmutableArray(), BuildReport());
        }

        private void RegisterUserMap(IMethodSymbol method, bool overwrite = false)
        {
            if (!method.IsStatic || method.IsGenericMethod || method.Parameters.Length != 1 || method.ReturnsVoid ||
                method.Parameters[0].RefKind != RefKind.None || method.ReturnsByRef || method.IsImplicitlyDeclared) return;
            var key = PairKey(method.Parameters[0].Type, method.ReturnType);
            if (overwrite || !_userMaps.ContainsKey(key)) _userMaps[key] = method;
            if (overwrite) _conversionMethods[key] = method;
        }

        private bool IsPartialDefinition(IMethodSymbol method)
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
                if (reference.GetSyntax(_ct) is MethodDeclarationSyntax syntax && syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
                    return true;
            return false;
        }

        private string TypeKeyword(INamedTypeSymbol type)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                switch (reference.GetSyntax(_ct))
                {
                    case RecordDeclarationSyntax record:
                        return record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record";
                    case StructDeclarationSyntax _:
                        return "struct";
                    case InterfaceDeclarationSyntax _:
                        return "interface";
                    case ClassDeclarationSyntax _:
                        return "class";
                }
            }
            return "class";
        }

        private static string TypeParameterList(INamedTypeSymbol type) =>
            type.TypeParameters.Length == 0 ? string.Empty : "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";

        private static string HintName(INamedTypeSymbol type)
        {
            var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            var sb = new StringBuilder(name.Length + 8);
            foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_');
            return sb.Append(".g.cs").ToString();
        }

        // ================================================================== mapping methods

        private void EmitMethod(IMethodSymbol method, MethodDeclarationSyntax syntax, CodeWriter w)
        {
            _location = syntax.Identifier.GetLocation();
            _current = method;
            w.Open(Signature(method, syntax));

            if (!method.IsGenericMethod && TryGetProjection(method, out var projectedSource, out var projectedDestination, out var isQueryable))
            {
                EmitProjection(w, method, projectedSource, projectedDestination, isQueryable);
                w.Close();
                return;
            }

            var isCreate = method.Parameters.Length == 1 && !method.ReturnsVoid;
            var isUpdate = method.Parameters.Length == 2 && method.ReturnsVoid;
            if (method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly ||
                method.Parameters.Any(p => p.RefKind != RefKind.None) || !(isCreate || isUpdate) ||
                (isUpdate && method.Parameters[1].Type.IsValueType))
            {
                Report(Diagnostics.UnsupportedSignature, method.Name);
                w.Line("throw new global::System.NotSupportedException(\"Unsupported mapping method signature.\");");
                w.Close();
                return;
            }

            var config = ReadConfig(method);
            var source = method.Parameters[0];
            if (isCreate)
            {
                EmitCreateBody(w, source.Type, method.ReturnType, Escape(source.Name), config, isRoot: true);
                CheckConfigUsage(config, method, method.ReturnType);
            }
            else
            {
                var target = method.Parameters[1];
                EmitUpdateBody(w, source.Type, target.Type, Escape(source.Name), Escape(target.Name), config, method.Name);
                CheckConfigUsage(config, method, target.Type);
            }
            w.Close();
        }

        private static string Signature(IMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            var sb = new StringBuilder();
            foreach (var modifier in syntax.Modifiers) sb.Append(modifier.Text).Append(' ');
            sb.Append(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(SignatureFormat));
            sb.Append(' ').Append(Escape(method.Name)).Append('(');
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                if (i == 0 && method.IsExtensionMethod) sb.Append("this ");
                var p = method.Parameters[i];
                sb.Append(p.Type.ToDisplayString(SignatureFormat)).Append(' ').Append(Escape(p.Name));
            }
            return sb.Append(')').ToString();
        }

        private static MemberConfig ReadConfig(IMethodSymbol method)
        {
            var config = new MemberConfig();
            foreach (var attribute in method.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                var args = attribute.ConstructorArguments;
                if (name == MapperGenerator.MapIgnoreAttributeName && args.Length == 1 && args[0].Value is string ignored)
                    config.Ignored.Add(ignored);
                else if (name == MapperGenerator.MapMemberAttributeName && args.Length == 2 && args[0].Value is string from && args[1].Value is string to)
                    config.Explicit[to] = from;
            }
            return config;
        }

        private void CheckConfigUsage(MemberConfig config, IMethodSymbol method, ITypeSymbol destination)
        {
            if (config.Ignored.Count == 0 && config.Explicit.Count == 0) return;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (destination is INamedTypeSymbol named)
            {
                foreach (var member in GetWritableMembers(named, false)) known.Add(member.Name);
                foreach (var ctor in named.InstanceConstructors)
                foreach (var p in ctor.Parameters) known.Add(p.Name);
            }
            foreach (var ignored in config.Ignored)
                if (!known.Contains(ignored)) Report(Diagnostics.UnknownAttributeMember, ignored, "MapIgnore", method.Name);
            foreach (var pair in config.Explicit)
                if (!known.Contains(pair.Key)) Report(Diagnostics.UnknownAttributeMember, pair.Key, "MapMember", method.Name);
        }

        // ================================================================== bodies

        /// <summary>Body of "TDest Map(TSource source)": null check, then object construction or a value conversion.</summary>
        private void EmitCreateBody(CodeWriter w, ITypeSymbol source, ITypeSymbol destination, string src, MemberConfig config, bool isRoot = false)
        {
            if (MemberResolver.IsNullableValueType(source, out var underlying))
            {
                w.Line("if (" + src + " is null) return default!;");
                src += ".Value";
                source = underlying;
            }
            else if (source.IsReferenceType)
            {
                w.Line("if (" + src + " is null) return default!;");
            }

            if (IsObjectLike(source, true) && IsConstructible(destination))
            {
                EmitObjectCreation(w, source, (INamedTypeSymbol)destination, src, config);
                return;
            }

            _atRoot = isRoot;
            var value = Convert(source, destination, src, 0);
            _atRoot = false;
            if (value == null)
            {
                Report(Diagnostics.IncompatibleTypes, src, Short(source), Short(destination), Short(destination), Short(destination));
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }
            w.Line("return " + value + ";");
        }

        private void EmitObjectCreation(CodeWriter w, ITypeSymbol source, INamedTypeSymbol destination, string src, MemberConfig config)
        {
            var ctor = ChooseConstructor(source, destination, src, config, out var arguments, out var consumed);
            if (ctor == null)
            {
                Report(Diagnostics.NoUsableConstructor, Short(destination), Short(source));
                w.Line("throw new global::System.InvalidOperationException(\"" + Short(destination) + " has no usable constructor.\");");
                return;
            }

            var assignments = new List<string>();
            foreach (var member in GetWritableMembers(destination, false))
            {
                if (config.Ignored.Contains(member.Name)) RecordIgnored(source, destination, member);
                if (consumed.Contains(member.Name) || config.Ignored.Contains(member.Name)) continue;
                var value = MapMember(source, src, member, destination, config);
                if (value != null) assignments.Add(Escape(member.Name) + " = " + value);
            }

            var creation = "new " + T(destination) + "(" + string.Join(", ", arguments) + ")";
            if (assignments.Count == 0)
            {
                w.Line("return " + creation + ";");
                return;
            }
            w.Line("return " + creation);
            w.Line("{");
            w.Indent();
            foreach (var assignment in assignments) w.Line(assignment + ",");
            w.Outdent();
            w.Line("};");
        }

        /// <summary>Body of "void Update(TSource source, TTarget target)": assigns every settable member of the existing target.</summary>
        private void EmitUpdateBody(CodeWriter w, ITypeSymbol source, ITypeSymbol target, string src, string dest, MemberConfig config, string methodName)
        {
            if (MemberResolver.IsNullableValueType(source, out var underlying))
            {
                w.Line("if (" + src + " is null) return;");
                src += ".Value";
                source = underlying;
            }
            else if (source.IsReferenceType)
            {
                w.Line("if (" + src + " is null) return;");
            }
            w.Line("if (" + dest + " is null) throw new global::System.ArgumentNullException(nameof(" + dest + "));");

            if (!IsObjectLike(source, true) || !IsObjectLike(target, true) || !(target is INamedTypeSymbol named))
            {
                Report(Diagnostics.UnsupportedSignature, methodName);
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }

            RecordMap(source, named);
            foreach (var member in GetWritableMembers(named, true))
            {
                if (config.Ignored.Contains(member.Name))
                {
                    RecordIgnored(source, named, member);
                    continue;
                }
                var value = MapMember(source, src, member, named, config);
                if (value != null) w.Line(dest + "." + Escape(member.Name) + " = " + value + ";");
            }
        }

        private IMethodSymbol? ChooseConstructor(ITypeSymbol source, INamedTypeSymbol destination, string src, MemberConfig config,
            out List<string> arguments, out HashSet<string> consumed)
        {
            var candidates = destination.InstanceConstructors
                .Where(c => IsAccessible(c) && !IsCopyConstructor(c, destination))
                .OrderByDescending(c => c.Parameters.Length)
                .ToList();

            RecordMap(source, destination);
            foreach (var ctor in candidates)
            {
                var args = new List<string>();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var chosenPaths = new List<KeyValuePair<IParameterSymbol, List<ISymbol>>>();
                var ok = true;
                foreach (var p in ctor.Parameters)
                {
                    if (config.Ignored.Contains(p.Name))
                    {
                        if (p.HasExplicitDefaultValue || p.IsParams) continue;
                        ok = false;
                        break;
                    }

                    string? value = null;
                    var path = config.Explicit.TryGetValue(p.Name, out var explicitPath)
                        ? _resolver.FindExplicitPath(source, explicitPath)
                        : _resolver.FindPath(source, p.Name);
                    if (path != null && path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(p) &&
                        !destination.GetMembers().Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && MemberResolver.IsSensitive(m)))
                    {
                        Report(Diagnostics.SensitiveLeak, Short(source) + "." + string.Join(".", path.Select(m => m.Name)), Short(destination) + "(" + p.Name + ")", p.Name);
                        path = null;
                    }
                    if (path != null)
                    {
                        var access = BuildPath(src, source, path);
                        value = Convert(access.Type, p.Type, access.Expression, 0);
                    }
                    if (value == null)
                    {
                        if (p.HasExplicitDefaultValue || p.IsParams) continue;
                        ok = false;
                        break;
                    }
                    args.Add(Escape(p.Name) + ": " + value);
                    used.Add(p.Name);
                    chosenPaths.Add(new KeyValuePair<IParameterSymbol, List<ISymbol>>(p, path!));
                }
                if (!ok) continue;
                foreach (var pair in chosenPaths) RecordFlow(source, destination, pair.Key.Name + " (constructor)", pair.Key, pair.Value, "constructor parameter");
                arguments = args;
                consumed = used;
                return ctor;
            }

            arguments = new List<string>();
            consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Structs can always be created with their implicit parameterless constructor.
            if (destination.IsValueType)
                return destination.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0) ?? candidates.FirstOrDefault() ?? destination.InstanceConstructors.FirstOrDefault();
            return null;
        }

        private static bool IsCopyConstructor(IMethodSymbol ctor, INamedTypeSymbol type) =>
            ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

        /// <summary>Settable instance properties and fields, most derived first. Init-only setters are excluded for updates.</summary>
        private List<ISymbol> GetWritableMembers(INamedTypeSymbol type, bool forUpdate)
        {
            var result = new List<ISymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (INamedTypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member.IsStatic || !member.CanBeReferencedByName) continue;
                    var ok = false;
                    switch (member)
                    {
                        case IPropertySymbol p:
                            ok = !p.IsIndexer && p.SetMethod != null && IsAccessible(p.SetMethod) && !(forUpdate && p.SetMethod.IsInitOnly);
                            break;
                        case IFieldSymbol f:
                            ok = !f.IsReadOnly && !f.IsConst && IsAccessible(f);
                            break;
                    }
                    if (ok && names.Add(member.Name)) result.Add(member);
                }
            }
            return result;
        }

        /// <summary>The value expression for one destination member, or null (a diagnostic has been reported).</summary>
        private string? MapMember(ITypeSymbol source, string src, ISymbol destinationMember, ITypeSymbol owner, MemberConfig config)
        {
            var destinationName = destinationMember.Name;
            var destinationType = MemberResolver.TypeOf(destinationMember);
            List<ISymbol>? path;
            if (config.Explicit.TryGetValue(destinationName, out var explicitPath))
            {
                path = _resolver.FindExplicitPath(source, explicitPath);
                if (path == null)
                {
                    Report(Diagnostics.UnknownAttributeMember, explicitPath, "MapMember", Short(source));
                    return null;
                }
            }
            else
            {
                path = _resolver.FindPath(source, destinationName);
                if (path == null)
                {
                    Report(Diagnostics.UnmappedMember, destinationName, Short(owner), Short(source));
                    return null;
                }
            }

            var sourceName = Short(source) + "." + string.Join(".", path.Select(m => m.Name));
            if (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(destinationMember))
            {
                Report(Diagnostics.SensitiveLeak, sourceName, Short(owner) + "." + destinationName, destinationName);
                return null;
            }

            var access = BuildPath(src, source, path);
            var value = Convert(access.Type, destinationType, access.Expression, 0);
            if (value == null)
            {
                Report(Diagnostics.IncompatibleTypes, sourceName, Short(access.Type), Short(owner) + "." + destinationName, Short(destinationType), destinationName);
                return null;
            }
            if (access.MayBeNull && !AcceptsNull(destinationType))
                Report(Diagnostics.NullableToNonNullable, sourceName, Short(owner) + "." + destinationName);
            RecordFlow(source, owner, destinationName, destinationMember, path, null);
            return value;
        }

        private static bool AcceptsNull(ITypeSymbol type) =>
            MemberResolver.IsNullableValueType(type, out _) || (type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated);

        private readonly struct MemberAccess
        {
            public MemberAccess(string expression, ITypeSymbol type, bool mayBeNull)
            {
                Expression = expression;
                Type = type;
                MayBeNull = mayBeNull;
            }

            public string Expression { get; }
            public ITypeSymbol Type { get; }
            public bool MayBeNull { get; }
        }

        /// <summary>source.Customer?.Address?.City: null-safe after every reference-type step; value results are lifted to Nullable.</summary>
        private MemberAccess BuildPath(string src, ITypeSymbol sourceType, List<ISymbol> path)
        {
            var sb = new StringBuilder(src);
            var current = sourceType;
            var lifted = false;
            var mayBeNull = false;
            for (var i = 0; i < path.Count; i++)
            {
                var member = path[i];
                if (i > 0 && current.IsReferenceType)
                {
                    sb.Append("?.");
                    lifted = true;
                    if (current.NullableAnnotation == NullableAnnotation.Annotated) mayBeNull = true;
                }
                else
                {
                    sb.Append('.');
                }
                sb.Append(Escape(member.Name));
                if (member is IMethodSymbol) sb.Append("()");
                current = MemberResolver.TypeOf(member);
            }

            var type = current;
            if (lifted && type.IsValueType && !MemberResolver.IsNullableValueType(type, out _))
                type = _nullableT.Construct(type);
            if (MemberResolver.IsNullableValueType(current, out _) || current.NullableAnnotation == NullableAnnotation.Annotated)
                mayBeNull = true;
            return new MemberAccess(sb.ToString(), type, mayBeNull);
        }

        // ================================================================== value conversions

        /// <summary>A C# expression converting <paramref name="expr"/> from <paramref name="s"/> to <paramref name="d"/>, or null if unsupported.</summary>
        private string? Convert(ITypeSymbol s, ITypeSymbol d, string expr, int depth)
        {
            if (depth > 8) return null;
            if (SymbolEqualityComparer.Default.Equals(s, d)) return expr;

            // A conversion method declared in the mapper class wins over the built-in conversions
            // (but a method never calls itself for its own root conversion).
            if (_userMaps.TryGetValue(PairKey(s, d), out var userMethod) &&
                !(depth == 0 && SymbolEqualityComparer.Default.Equals(userMethod, _current) && _atRoot))
                return Escape(userMethod.Name) + "(" + expr + ")";

            var conversion = _compilation.ClassifyConversion(s, d);
            if (conversion.Exists && conversion.IsImplicit) return expr;

            var sIsNullable = MemberResolver.IsNullableValueType(s, out var sUnderlying);
            var dIsNullable = MemberResolver.IsNullableValueType(d, out var dUnderlying);

            if (d.SpecialType == SpecialType.System_String)
            {
                if (IsScalar(sUnderlying))
                    return s.IsReferenceType || sIsNullable ? expr + "?.ToString()" : expr + ".ToString()";
            }
            else if (sIsNullable && dIsNullable)
            {
                var v = NewVariable();
                var inner = Convert(sUnderlying, dUnderlying, v, depth + 1);
                return inner == null ? null : "(" + expr + " is " + T(sUnderlying) + " " + v + " ? (" + T(d) + ")(" + inner + ") : default(" + T(d) + "))";
            }
            else if (sIsNullable)
            {
                return Convert(sUnderlying, d, "(" + expr + ").GetValueOrDefault()", depth + 1);
            }
            else if (dIsNullable)
            {
                var inner = Convert(s, dUnderlying, expr, depth + 1);
                return inner == null ? null : "(" + T(d) + ")(" + inner + ")";
            }

            if (s.TypeKind == TypeKind.Enum && d.TypeKind == TypeKind.Enum) return EnumHelper((INamedTypeSymbol)s, (INamedTypeSymbol)d) + "(" + expr + ")";
            if (s.SpecialType == SpecialType.System_String && d.TypeKind == TypeKind.Enum)
                return "(" + T(d) + ")global::System.Enum.Parse(typeof(" + T(d) + "), " + expr + ", true)";
            if ((s.TypeKind == TypeKind.Enum && IsIntegral(d)) || (IsIntegral(s) && d.TypeKind == TypeKind.Enum))
                return "(" + T(d) + ")(" + expr + ")";

            if (s.SpecialType == SpecialType.System_String)
            {
                var parse = ParseCall(d, expr);
                if (parse != null) return parse;
            }

            if (conversion.Exists && conversion.IsExplicit && conversion.IsNumeric)
                return "checked((" + T(d) + ")(" + expr + "))";

            var kind = GetCollectionKind(d);
            var sourceElement = ElementTypeOf(s);
            if (kind != CollectionKind.None && sourceElement != null)
                return CollectionHelper(s, d, sourceElement, kind, depth) is string helper ? helper + "(" + expr + ")" : null;

            if (IsObjectLike(s, true) && IsConstructible(d))
                return ObjectHelper(s, (INamedTypeSymbol)d) + "(" + expr + ")";

            if (conversion.Exists && conversion.IsExplicit && conversion.IsUserDefined)
                return "(" + T(d) + ")(" + expr + ")";

            return null;
        }

        private static string? ParseCall(ITypeSymbol d, string expr)
        {
            IMethodSymbol? withProvider = null, plain = null;
            foreach (var member in d.GetMembers("Parse"))
            {
                if (!(member is IMethodSymbol m) || !m.IsStatic || m.DeclaredAccessibility != Accessibility.Public ||
                    !SymbolEqualityComparer.Default.Equals(m.ReturnType, d)) continue;
                if (m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String) plain = m;
                if (m.Parameters.Length == 2 && m.Parameters[0].Type.SpecialType == SpecialType.System_String &&
                    m.Parameters[1].Type.ToDisplayString() == "System.IFormatProvider") withProvider = m;
            }
            if (withProvider != null) return T(d) + ".Parse(" + expr + ", global::System.Globalization.CultureInfo.InvariantCulture)";
            if (plain != null) return T(d) + ".Parse(" + expr + ")";
            return null;
        }

        // ================================================================== helpers (emitted once per type pair)

        private string ObjectHelper(ITypeSymbol source, INamedTypeSymbol destination)
        {
            var key = "object:" + PairKey(source, destination);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;

            var name = NewHelperName("Map", source, destination);
            _helperNames[key] = name; // registered first so cyclic graphs call themselves

            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Open("private static " + T(destination) + " " + name + "(" + T(source) + " source)");
            EmitCreateBody(w, source, destination, "source", MemberConfig.Empty);
            w.Close();
            _helpers.Append(w.ToString());
            return name;
        }

        private string EnumHelper(INamedTypeSymbol source, INamedTypeSymbol destination)
        {
            var key = "enum:" + PairKey(source, destination);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = NewHelperName("MapEnum", source, destination);
            _helperNames[key] = name;

            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Open("private static " + T(destination) + " " + name + "(" + T(source) + " value)");
            if (HasFlags(source) || HasFlags(destination))
            {
                // [Flags] combinations have no single name: convert through the numeric value.
                w.Line("return (" + T(destination) + ")(" + T(source.EnumUnderlyingType!) + ")value;");
            }
            else
            {
                var targets = destination.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
                var seen = new HashSet<object>();
                w.Open("switch (value)");
                foreach (var field in source.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                {
                    if (field.ConstantValue == null || !seen.Add(field.ConstantValue)) continue; // aliases share a case label
                    var match = targets.FirstOrDefault(t => string.Equals(t.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Report(Diagnostics.UnmappedEnumValue, source.Name, field.Name, destination.Name);
                        continue;
                    }
                    w.Line("case " + T(source) + "." + Escape(field.Name) + ": return " + T(destination) + "." + Escape(match.Name) + ";");
                }
                w.Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(value), value, \"No " + destination.Name + " value matches this " + source.Name + " value.\");");
                w.Close();
            }
            w.Close();
            _helpers.Append(w.ToString());
            return name;
        }

        private string? CollectionHelper(ITypeSymbol source, ITypeSymbol destination, ITypeSymbol sourceElement, CollectionKind kind, int depth)
        {
            var key = "collection:" + PairKey(source, destination);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;

            var w = new CodeWriter(_memberIndent);
            var sourceDefinition = source is INamedTypeSymbol sn ? sn.OriginalDefinition.ToDisplayString() : string.Empty;
            var isArraySource = source is IArrayTypeSymbol;
            var indexable = isArraySource || sourceDefinition == "System.Collections.Generic.List<T>" ||
                            sourceDefinition == "System.Collections.Generic.IList<T>" || sourceDefinition == "System.Collections.Generic.IReadOnlyList<T>";
            var countable = indexable || sourceDefinition == "System.Collections.Generic.ICollection<T>" ||
                            sourceDefinition == "System.Collections.Generic.IReadOnlyCollection<T>" || sourceDefinition == "System.Collections.Generic.HashSet<T>" ||
                            sourceDefinition == "System.Collections.Generic.Dictionary<TKey, TValue>" ||
                            sourceDefinition == "System.Collections.Generic.IDictionary<TKey, TValue>" ||
                            sourceDefinition == "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
            var count = isArraySource ? "source.Length" : countable ? "source.Count" : null;
            var name = NewHelperName("MapCollection", source, destination);

            w.Line();
            w.Open("private static " + T(destination) + " " + name + "(" + T(source) + " source)");

            if (kind == CollectionKind.Dictionary)
            {
                var d = (INamedTypeSymbol)destination;
                if (!(sourceElement is INamedTypeSymbol pair) || pair.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.KeyValuePair<TKey, TValue>")
                    return null;
                var keyValue = Convert(pair.TypeArguments[0], d.TypeArguments[0], "item.Key", depth + 1);
                var valueValue = Convert(pair.TypeArguments[1], d.TypeArguments[1], "item.Value", depth + 1);
                if (keyValue == null || valueValue == null) return null;
                var dictionary = "global::System.Collections.Generic.Dictionary<" + T(d.TypeArguments[0]) + ", " + T(d.TypeArguments[1]) + ">";
                if (source.IsReferenceType) w.Line("if (source is null) return new " + dictionary + "();");
                w.Line("var result = new " + dictionary + "(" + (count ?? string.Empty) + ");");
                w.Line("foreach (var item in source) result[" + keyValue + "] = " + valueValue + ";");
                w.Line("return result;");
            }
            else
            {
                var destinationElement = destination is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)destination).TypeArguments[0];
                var element = Convert(sourceElement, destinationElement, "item", depth + 1);
                if (element == null) return null;
                var elementType = T(destinationElement);
                var list = "global::System.Collections.Generic.List<" + elementType + ">";
                var set = "global::System.Collections.Generic.HashSet<" + elementType + ">";

                if (kind == CollectionKind.Array)
                {
                    if (source.IsReferenceType) w.Line("if (source is null) return global::System.Array.Empty<" + elementType + ">();");
                    if (indexable && !(destinationElement is IArrayTypeSymbol))
                    {
                        w.Line("var result = new " + elementType + "[" + count + "];");
                        w.Open("for (var i = 0; i < result.Length; i++)");
                        w.Line("var item = source[i];");
                        w.Line("result[i] = " + element + ";");
                        w.Close();
                        w.Line("return result;");
                    }
                    else
                    {
                        w.Line("var result = new " + list + "(" + (count ?? string.Empty) + ");");
                        w.Line("foreach (var item in source) result.Add(" + element + ");");
                        w.Line("return result.ToArray();");
                    }
                }
                else if (kind == CollectionKind.HashSet)
                {
                    if (source.IsReferenceType) w.Line("if (source is null) return new " + set + "();");
                    w.Line("var result = new " + set + "();");
                    w.Line("foreach (var item in source) result.Add(" + element + ");");
                    w.Line("return result;");
                }
                else
                {
                    if (source.IsReferenceType) w.Line("if (source is null) return new " + list + "();");
                    w.Line("var result = new " + list + "(" + (count ?? string.Empty) + ");");
                    if (indexable)
                    {
                        w.Open("for (var i = 0; i < " + count + "; i++)");
                        w.Line("var item = source[i];");
                        w.Line("result.Add(" + element + ");");
                        w.Close();
                    }
                    else
                    {
                        w.Line("foreach (var item in source) result.Add(" + element + ");");
                    }
                    w.Line("return result;");
                }
            }

            w.Close();
            _helperNames[key] = name;
            _helpers.Append(w.ToString());
            return name;
        }

        // ================================================================== projections (IQueryable / expression trees)

        /// <summary>
        /// IQueryable&lt;TDest&gt; M(IQueryable&lt;TSource&gt;) or Expression&lt;Func&lt;TSource, TDest&gt;&gt; M().
        /// </summary>
        private static bool TryGetProjection(IMethodSymbol method, out ITypeSymbol source, out ITypeSymbol destination, out bool isQueryable)
        {
            source = destination = null!;
            isQueryable = false;
            if (method.ReturnsVoid || !(method.ReturnType is INamedTypeSymbol returnType) || !returnType.IsGenericType) return false;
            var definition = returnType.OriginalDefinition.ToDisplayString();

            if (definition == "System.Linq.IQueryable<T>" && method.Parameters.Length == 1 &&
                method.Parameters[0].Type is INamedTypeSymbol parameter && parameter.IsGenericType &&
                parameter.OriginalDefinition.ToDisplayString() == "System.Linq.IQueryable<T>")
            {
                source = parameter.TypeArguments[0];
                destination = returnType.TypeArguments[0];
                isQueryable = true;
                return true;
            }

            if (definition == "System.Linq.Expressions.Expression<TDelegate>" && method.Parameters.Length == 0 &&
                returnType.TypeArguments[0] is INamedTypeSymbol func && func.IsGenericType &&
                func.OriginalDefinition.ToDisplayString() == "System.Func<T, TResult>")
            {
                source = func.TypeArguments[0];
                destination = func.TypeArguments[1];
                return true;
            }
            return false;
        }

        /// <summary>
        /// Emits a static expression (x =&gt; new TDest { ... }) that LINQ providers such as EF Core translate to SQL:
        /// only the needed columns are selected. Expression trees cannot contain ?., patterns, named arguments or
        /// statements, so projections are generated separately: navigation is plain (the provider handles nulls),
        /// nested objects are created inline with a null check, collections use Enumerable.Select/ToList.
        /// </summary>
        private void EmitProjection(CodeWriter w, IMethodSymbol method, ITypeSymbol source, ITypeSymbol destination, bool isQueryable)
        {
            var config = ReadConfig(method);
            if (!IsObjectLike(source, true) || !IsConstructible(destination))
            {
                Report(Diagnostics.UnsupportedSignature, method.Name);
                w.Line("throw new global::System.NotSupportedException(\"Unsupported projection.\");");
                return;
            }

            var stack = new HashSet<string>(StringComparer.Ordinal) { PairKey(source, destination) };
            var body = ProjectionObject(source, (INamedTypeSymbol)destination, "x", config, 0, stack);
            CheckConfigUsage(config, method, destination);
            if (body == null)
            {
                w.Line("throw new global::System.NotSupportedException(\"" + Short(destination) + " has no usable constructor.\");");
                return;
            }

            var field = "__projection" + (++_counter).ToString(CultureInfo.InvariantCulture) + "_" + Identifier(source) + "_" + Identifier(destination);
            var expressionType = "global::System.Linq.Expressions.Expression<global::System.Func<" + T(source) + ", " + T(destination) + ">>";
            var helper = new CodeWriter(_memberIndent);
            helper.Line();
            helper.Line("private static readonly " + expressionType + " " + field + " =");
            helper.Indent();
            helper.Line("x => " + body + ";");
            helper.Outdent();
            _helpers.Append(helper.ToString());

            w.Line(isQueryable
                ? "return global::System.Linq.Queryable.Select(" + Escape(method.Parameters[0].Name) + ", " + field + ");"
                : "return " + field + ";");
        }

        private string? ProjectionObject(ITypeSymbol source, INamedTypeSymbol destination, string src, MemberConfig config, int depth, HashSet<string> stack)
        {
            // Constructor: expression trees allow neither named nor omitted optional arguments, so every parameter is mapped in order.
            IMethodSymbol? chosen = null;
            var arguments = new List<string>();
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ctor in destination.InstanceConstructors.Where(c => IsAccessible(c) && !IsCopyConstructor(c, destination)).OrderByDescending(c => c.Parameters.Length))
            {
                var args = new List<string>();
                var ok = true;
                foreach (var p in ctor.Parameters)
                {
                    var path = config.Ignored.Contains(p.Name) ? null
                        : config.Explicit.TryGetValue(p.Name, out var explicitPath) ? _resolver.FindExplicitPath(source, explicitPath)
                        : _resolver.FindPath(source, p.Name);
                    if (path == null || (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(p) &&
                                         !destination.GetMembers().Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && MemberResolver.IsSensitive(m))))
                    {
                        ok = false;
                        break;
                    }
                    var value = ProjectionValue(MemberResolver.TypeOf(path[path.Count - 1]), p.Type, ProjectionPath(src, path), depth, stack);
                    if (value == null)
                    {
                        ok = false;
                        break;
                    }
                    args.Add(value);
                }
                if (!ok) continue;
                chosen = ctor;
                arguments = args;
                foreach (var p in ctor.Parameters)
                {
                    var used = config.Explicit.TryGetValue(p.Name, out var explicitUsed) ? _resolver.FindExplicitPath(source, explicitUsed) : _resolver.FindPath(source, p.Name);
                    if (used != null) RecordFlow(source, destination, p.Name + " (constructor)", p, used, "projection, constructor parameter");
                }
                foreach (var p in ctor.Parameters) consumed.Add(p.Name);
                break;
            }
            if (chosen == null && !destination.IsValueType)
            {
                Report(Diagnostics.NoUsableConstructor, Short(destination), Short(source));
                return null;
            }

            RecordMap(source, destination);
            var assignments = new List<string>();
            foreach (var member in GetWritableMembers(destination, false))
            {
                if (config.Ignored.Contains(member.Name)) RecordIgnored(source, destination, member);
                if (consumed.Contains(member.Name) || config.Ignored.Contains(member.Name)) continue;
                var value = ProjectionMember(source, src, member, destination, config, depth, stack);
                if (value != null) assignments.Add(Escape(member.Name) + " = " + value);
            }

            var creation = "new " + T(destination) + "(" + string.Join(", ", arguments) + ")";
            return assignments.Count == 0 ? creation : creation + " { " + string.Join(", ", assignments) + " }";
        }

        private string? ProjectionMember(ITypeSymbol source, string src, ISymbol destinationMember, ITypeSymbol owner, MemberConfig config, int depth, HashSet<string> stack)
        {
            var name = destinationMember.Name;
            List<ISymbol>? path;
            if (config.Explicit.TryGetValue(name, out var explicitPath))
            {
                path = _resolver.FindExplicitPath(source, explicitPath);
                if (path == null)
                {
                    Report(Diagnostics.UnknownAttributeMember, explicitPath, "MapMember", Short(source));
                    return null;
                }
            }
            else
            {
                path = _resolver.FindPath(source, name);
                if (path == null)
                {
                    Report(Diagnostics.UnmappedMember, name, Short(owner), Short(source));
                    return null;
                }
            }

            var sourceName = Short(source) + "." + string.Join(".", path.Select(m => m.Name));
            if (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(destinationMember))
            {
                Report(Diagnostics.SensitiveLeak, sourceName, Short(owner) + "." + name, name);
                return null;
            }

            var valueType = MemberResolver.TypeOf(path[path.Count - 1]);
            var destinationType = MemberResolver.TypeOf(destinationMember);
            var value = ProjectionValue(valueType, destinationType, ProjectionPath(src, path), depth, stack);
            if (value == null) Report(Diagnostics.IncompatibleTypes, sourceName, Short(valueType), Short(owner) + "." + name, Short(destinationType), name);
            else RecordFlow(source, owner, name, destinationMember, path, "projection");
            return value;
        }

        /// <summary>x.Customer.Address.City: plain navigation; the LINQ provider handles nulls in SQL.</summary>
        private static string ProjectionPath(string src, List<ISymbol> path)
        {
            var sb = new StringBuilder(src);
            foreach (var member in path)
            {
                sb.Append('.').Append(Escape(member.Name));
                if (member is IMethodSymbol) sb.Append("()");
            }
            return sb.ToString();
        }

        /// <summary>Expression-tree-safe conversion, or null when unsupported.</summary>
        private string? ProjectionValue(ITypeSymbol s, ITypeSymbol d, string expr, int depth, HashSet<string> stack)
        {
            if (depth > 8) return null;
            if (SymbolEqualityComparer.Default.Equals(s, d)) return expr;
            if (_conversionMethods.TryGetValue(PairKey(s, d), out var method)) return Escape(method.Name) + "(" + expr + ")";

            var conversion = _compilation.ClassifyConversion(s, d);
            if (conversion.Exists && conversion.IsImplicit) return expr;

            var sIsNullable = MemberResolver.IsNullableValueType(s, out var sUnderlying);
            var dIsNullable = MemberResolver.IsNullableValueType(d, out var dUnderlying);

            if (d.SpecialType == SpecialType.System_String)
            {
                if (!IsScalar(sUnderlying)) return null;
                return sIsNullable ? "(" + expr + " == null ? null : " + expr + ".Value.ToString())" : expr + ".ToString()";
            }
            if (sIsNullable && dIsNullable)
            {
                var inner = ProjectionValue(sUnderlying, dUnderlying, expr + ".Value", depth + 1, stack);
                return inner == null ? null : "(" + expr + " == null ? (" + T(d) + ")null : (" + T(d) + ")(" + inner + "))";
            }
            if (sIsNullable) return ProjectionValue(sUnderlying, d, "(" + expr + " ?? default(" + T(sUnderlying) + "))", depth + 1, stack);
            if (dIsNullable)
            {
                var inner = ProjectionValue(s, dUnderlying, expr, depth + 1, stack);
                return inner == null ? null : "(" + T(d) + ")(" + inner + ")";
            }

            if (s.TypeKind == TypeKind.Enum && d.TypeKind == TypeKind.Enum) return ProjectionEnum((INamedTypeSymbol)s, (INamedTypeSymbol)d, expr);
            if ((s.TypeKind == TypeKind.Enum && IsIntegral(d)) || (IsIntegral(s) && d.TypeKind == TypeKind.Enum)) return "(" + T(d) + ")(" + expr + ")";
            if (conversion.Exists && conversion.IsExplicit && conversion.IsNumeric) return "(" + T(d) + ")(" + expr + ")";

            var kind = GetCollectionKind(d);
            var sourceElement = ElementTypeOf(s);
            if ((kind == CollectionKind.List || kind == CollectionKind.Array) && sourceElement != null)
            {
                var destinationElement = d is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)d).TypeArguments[0];
                var parameter = "x" + (++_counter).ToString(CultureInfo.InvariantCulture);
                var element = ProjectionValue(sourceElement, destinationElement, parameter, depth + 1, stack);
                if (element == null) return null;
                var items = element == parameter
                    ? expr
                    : "global::System.Linq.Enumerable.Select(" + expr + ", " + parameter + " => " + element + ")";
                return (kind == CollectionKind.Array ? "global::System.Linq.Enumerable.ToArray(" : "global::System.Linq.Enumerable.ToList(") + items + ")";
            }

            if (IsObjectLike(s, true) && IsConstructible(d))
            {
                var key = PairKey(s, d);
                if (stack.Contains(key))
                {
                    Report(Diagnostics.RecursiveProjection, Short(d));
                    return d.IsValueType ? null : "(" + T(d) + ")null";
                }
                stack.Add(key);
                var created = ProjectionObject(s, (INamedTypeSymbol)d, expr, MemberConfig.Empty, depth + 1, stack);
                stack.Remove(key);
                if (created == null) return null;
                return s.IsReferenceType ? "(" + expr + " == null ? null : " + created + ")" : created;
            }

            if (conversion.Exists && conversion.IsExplicit && conversion.IsUserDefined) return "(" + T(d) + ")(" + expr + ")";
            return null;
        }

        /// <summary>Enum by name as a conditional chain (translates to CASE WHEN in SQL).</summary>
        private string ProjectionEnum(INamedTypeSymbol source, INamedTypeSymbol destination, string expr)
        {
            if (HasFlags(source) || HasFlags(destination)) return "(" + T(destination) + ")(" + T(source.EnumUnderlyingType!) + ")(" + expr + ")";
            var targets = destination.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
            var seen = new HashSet<object>();
            var report = _reportedEnumPairs.Add(PairKey(source, destination));
            var result = "default(" + T(destination) + ")";
            var cases = new List<string>();
            foreach (var field in source.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
            {
                if (field.ConstantValue == null || !seen.Add(field.ConstantValue)) continue;
                var match = targets.FirstOrDefault(t => string.Equals(t.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    if (report) Report(Diagnostics.UnmappedEnumValue, source.Name, field.Name, destination.Name);
                    continue;
                }
                cases.Add(T(source) + "." + Escape(field.Name) + "|" + T(destination) + "." + Escape(match.Name));
            }
            for (var i = cases.Count - 1; i >= 0; i--)
            {
                var parts = cases[i].Split('|');
                result = "(" + expr + " == " + parts[0] + " ? " + parts[1] + " : " + result + ")";
            }
            return result;
        }

        // ================================================================== type classification

        private static CollectionKind GetCollectionKind(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) return array.Rank == 1 ? CollectionKind.Array : CollectionKind.None;
            if (!(type is INamedTypeSymbol named) || !named.IsGenericType) return CollectionKind.None;
            switch (named.OriginalDefinition.ToDisplayString())
            {
                case "System.Collections.Generic.List<T>":
                case "System.Collections.Generic.IList<T>":
                case "System.Collections.Generic.ICollection<T>":
                case "System.Collections.Generic.IEnumerable<T>":
                case "System.Collections.Generic.IReadOnlyList<T>":
                case "System.Collections.Generic.IReadOnlyCollection<T>":
                    return CollectionKind.List;
                case "System.Collections.Generic.HashSet<T>":
                case "System.Collections.Generic.ISet<T>":
                    return CollectionKind.HashSet;
                case "System.Collections.Generic.Dictionary<TKey, TValue>":
                case "System.Collections.Generic.IDictionary<TKey, TValue>":
                case "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>":
                    return CollectionKind.Dictionary;
                default:
                    return CollectionKind.None;
            }
        }

        private static ITypeSymbol? ElementTypeOf(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_String) return null;
            if (type is IArrayTypeSymbol array) return array.Rank == 1 ? array.ElementType : null;
            if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return named.TypeArguments[0];
            foreach (var i in type.AllInterfaces)
                if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) return i.TypeArguments[0];
            return null;
        }

        /// <summary>Classes, structs (and, for sources, interfaces) that are mapped member by member.</summary>
        private static bool IsObjectLike(ITypeSymbol type, bool allowInterface)
        {
            if (!(type is INamedTypeSymbol named)) return false;
            if (named.TypeKind != TypeKind.Class && named.TypeKind != TypeKind.Struct && !(allowInterface && named.TypeKind == TypeKind.Interface)) return false;
            if (named.SpecialType != SpecialType.None || MemberResolver.IsNullableValueType(named, out _)) return false;
            if (IsSystemType(named) || ElementTypeOf(named) != null) return false;
            return true;
        }

        private static bool IsConstructible(ITypeSymbol type) => IsObjectLike(type, false) && !type.IsAbstract;

        private static bool IsSystemType(INamedTypeSymbol type) => type.ContainingNamespace?.ToDisplayString() == "System";

        private static bool IsScalar(ITypeSymbol type) =>
            (type.SpecialType != SpecialType.None && type.SpecialType != SpecialType.System_Object) ||
            type.TypeKind == TypeKind.Enum ||
            (type is INamedTypeSymbol named && type.IsValueType && IsSystemType(named));

        private static bool IsIntegral(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasFlags(INamedTypeSymbol type) =>
            type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");

        // ================================================================== data-flow report

        private SortedDictionary<string, string> RecordMap(ITypeSymbol source, ITypeSymbol destination)
        {
            var key = "`" + Short(source) + "` → `" + Short(destination) + "`";
            if (!_flows.TryGetValue(key, out var rows))
            {
                rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
                _flows[key] = rows;
            }
            foreach (var member in _resolver.GetReadableMembers(source))
                if (MemberResolver.IsSensitive(member)) SensitiveTargets(member);
            return rows;
        }

        private void RecordIgnored(ITypeSymbol source, ITypeSymbol destination, ISymbol member) =>
            RecordMap(source, destination)[member.Name] =
                "| " + DestinationCell(member.Name, member) + " | — | ignored |";

        private void RecordFlow(ITypeSymbol source, ITypeSymbol destination, string destinationName, ISymbol destinationSymbol, List<ISymbol> path, string? note)
        {
            var notes = new List<string>();
            if (note != null) notes.Add(note);
            if (path.Count > 1) notes.Add("flattened");
            foreach (var member in path.Where(MemberResolver.IsSensitive))
            {
                notes.Add("sensitive");
                SensitiveTargets(member).Add(Short(destination) + "." + destinationName.Replace(" (constructor)", string.Empty));
            }
            var description = "`" + string.Join(".", path.Select(m => m is IMethodSymbol ? m.Name + "()" : m.Name)) + "`";
            RecordMap(source, destination)[destinationName] =
                "| " + DestinationCell(destinationName, destinationSymbol) + " | " + description + " | " + string.Join(", ", notes) + " |";
        }

        private static string DestinationCell(string name, ISymbol symbol) => MemberResolver.IsSensitive(symbol) ? name + " (sensitive)" : name;

        private SortedSet<string> SensitiveTargets(ISymbol member)
        {
            var key = (member.ContainingType != null ? Short(member.ContainingType) + "." : string.Empty) + member.Name;
            if (!_sensitive.TryGetValue(key, out var targets))
            {
                targets = new SortedSet<string>(StringComparer.Ordinal);
                _sensitive[key] = targets;
            }
            return targets;
        }

        /// <summary>The Markdown section for this mapper class (combined per assembly into ConventionMapper.Generated.DataFlowReport).</summary>
        private string? BuildReport()
        {
            if (_flows.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append("## `").Append(_mapper.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)).AppendLine("`").AppendLine();
            if (_sensitive.Count > 0)
            {
                sb.AppendLine("Sensitive members:").AppendLine();
                sb.AppendLine("| Member | Copied to |");
                sb.AppendLine("| --- | --- |");
                foreach (var pair in _sensitive)
                {
                    var targets = pair.Value.Count == 0 ? "not copied anywhere" : string.Join(", ", pair.Value.Select(t => "`" + t + "` (protected)"));
                    sb.Append("| `").Append(pair.Key).Append("` | ").Append(targets).AppendLine(" |");
                }
                sb.AppendLine();
            }
            foreach (var map in _flows)
            {
                sb.Append("### ").AppendLine(map.Key).AppendLine();
                if (map.Value.Count == 0)
                {
                    sb.AppendLine("Converted as a whole (no member-by-member flows).").AppendLine();
                    continue;
                }
                sb.AppendLine("| Destination | Source | Notes |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var row in map.Value.Values) sb.AppendLine(row);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ================================================================== small utilities

        private static string T(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

        private static string Short(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        private static string PairKey(ITypeSymbol source, ITypeSymbol destination) => T(source) + "->" + T(destination);

        private string NewVariable() => "__v" + (++_counter).ToString(CultureInfo.InvariantCulture);

        private string NewHelperName(string prefix, ITypeSymbol source, ITypeSymbol destination) =>
            "__" + prefix + (++_counter).ToString(CultureInfo.InvariantCulture) + "_" + Identifier(source) + "_" + Identifier(destination);

        private static string Identifier(ITypeSymbol type)
        {
            var sb = new StringBuilder();
            foreach (var c in type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string Escape(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private void Report(DiagnosticDescriptor descriptor, params object[] args) =>
            _diagnostics.Add(Diagnostic.Create(descriptor, _location, args));

        private sealed class MemberConfig
        {
            public static readonly MemberConfig Empty = new MemberConfig();

            public HashSet<string> Ignored { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Explicit { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Indented source builder.</summary>
    internal sealed class CodeWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        public CodeWriter(int indent)
        {
            _indent = indent;
        }

        public CodeWriter Line(string text = "")
        {
            if (text.Length > 0) _sb.Append(' ', _indent * 4).Append(text);
            _sb.Append('\n');
            return this;
        }

        public CodeWriter Open(string header)
        {
            Line(header);
            Line("{");
            _indent++;
            return this;
        }

        public CodeWriter Close()
        {
            _indent--;
            return Line("}");
        }

        public void Indent() => _indent++;

        public void Outdent() => _indent--;

        /// <summary>Appends text that is already indented.</summary>
        public void Raw(string text) => _sb.Append(text);

        public override string ToString() => _sb.ToString();
    }
}
