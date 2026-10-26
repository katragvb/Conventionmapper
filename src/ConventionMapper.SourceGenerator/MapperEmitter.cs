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
        private readonly HashSet<string> _reportedAmbiguities = new HashSet<string>(StringComparer.Ordinal);

        // Class-level settings from [GeneratedMapper] and [MapAllowParse] on the class.
        private bool _requireMapConversion;
        private string _provider = ProjectionProvider.Default;
        private readonly HashSet<string> _classAllowParse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _classReviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _currentReviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ordinary static methods marked [MapConversion] (no CM0017 for these).</summary>
        private readonly HashSet<IMethodSymbol> _markedConversions = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        /// <summary>Conversion method -> "Owner.Member" it was applied to (report column and CM0017).</summary>
        private readonly Dictionary<IMethodSymbol, SortedSet<string>> _conversionUses = new Dictionary<IMethodSymbol, SortedSet<string>>(SymbolEqualityComparer.Default);

        /// <summary>What the value conversion of the member being mapped did: risky conversions and user methods it called.</summary>
        private Trace? _trace;
        private readonly Dictionary<string, Trace> _helperTraces = new Dictionary<string, Trace>(StringComparer.Ordinal);

        // Data-flow report: section key -> section (method sections first, by name then declaration order; nested maps after);
        // sensitive member -> destinations; flows a reviewer should look at first.
        private readonly SortedDictionary<string, FlowSection> _flows = new SortedDictionary<string, FlowSection>(StringComparer.Ordinal);
        private readonly SortedSet<string> _riskyFlows = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>Destination members each mapping method writes (top level), for the generated WriteSets and the method docs.</summary>
        private readonly Dictionary<IMethodSymbol, SortedSet<string>> _writeSets = new Dictionary<IMethodSymbol, SortedSet<string>>(SymbolEqualityComparer.Default);
        private readonly Dictionary<IMethodSymbol, int> _methodOrder = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        private ITypeSymbol? _rootSource;
        private ITypeSymbol? _rootDestination;
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
            ReadClassSettings();
            // Methods named by [MapFrom] compute one member; they are not conversions for every pair of their types.
            var computeMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in methods)
                foreach (var attribute in pair.Key.GetAttributes())
                    if (attribute.AttributeClass?.ToDisplayString() == MapperGenerator.MapFromAttributeName && attribute.ConstructorArguments.Length == 2 &&
                        attribute.ConstructorArguments[0].Value is string computeName)
                        computeMethods.Add(computeName);
            // Log, redact and diff methods have their own meaning; they are never conversions.
            foreach (var pair in methods)
                if (!HasAttribute(pair.Key, MapperGenerator.MapLogStringAttributeName) && !HasAttribute(pair.Key, MapperGenerator.MapRedactAttributeName))
                    RegisterUserMap(pair.Key);
            foreach (var member in _mapper.GetMembers())
            {
                if (!(member is IMethodSymbol method) || method.MethodKind != MethodKind.Ordinary || IsPartialDefinition(method)) continue;
                if (computeMethods.Contains(method.Name) && !HasAttribute(method, MapperGenerator.MapConversionAttributeName)) continue;
                var marked = HasAttribute(method, MapperGenerator.MapConversionAttributeName);
                if (marked)
                {
                    if (!IsConversionShape(method))
                    {
                        _diagnostics.Add(Diagnostic.Create(Diagnostics.InvalidMapConversion, method.Locations.FirstOrDefault() ?? node.Identifier.GetLocation(), method.Name));
                        continue;
                    }
                    _markedConversions.Add(method);
                }
                else if (_requireMapConversion) continue;
                RegisterUserMap(method, overwrite: true);
            }

            var hasNamespace = !_mapper.ContainingNamespace.IsGlobalNamespace;
            _memberIndent = (hasNamespace ? 1 : 0) + chain.Count;

            for (var i = 0; i < methods.Count; i++) _methodOrder[methods[i].Key] = i;
            var body = new CodeWriter(_memberIndent);
            var first = true;
            foreach (var pair in methods)
            {
                _ct.ThrowIfCancellationRequested();
                if (!first) body.Line();
                first = false;
                EmitMethod(pair.Key, pair.Value, body);
            }
            EmitWriteSets(methods.Select(m => m.Key).ToList(), body);

            var file = new CodeWriter(0);
            file.Line("// <auto-generated/>");
            file.Line("// Generated by ConventionMapper.SourceGenerator. Do not edit.");
            file.Line("#nullable enable");
            file.Line("#pragma warning disable");
            foreach (var ns in _extraUsings) file.Line("using " + ns + ";"); // extension methods used by inlined [MapFrom] bodies
            file.Line();
            if (hasNamespace) file.Open("namespace " + _mapper.ContainingNamespace.ToDisplayString());
            foreach (var type in chain) file.Open("partial " + TypeKeyword(type) + " " + type.Name + TypeParameterList(type));
            file.Raw(body.ToString());
            file.Raw(_helpers.ToString());
            foreach (var _ in chain) file.Close();
            if (hasNamespace) file.Close();

            ReportUnmarkedConversions();
            var report = BuildReport();
            return new MapperResult(hintName, file.ToString(), _diagnostics.ToImmutableArray(), report,
                _mapper.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                report == null ? null : BuildCsv(), report == null ? null : BuildJson());
        }

        private static bool IsConversionShape(IMethodSymbol method) =>
            method.IsStatic && !method.IsGenericMethod && method.Parameters.Length == 1 && !method.ReturnsVoid &&
            method.Parameters[0].RefKind == RefKind.None && !method.ReturnsByRef && !method.ReturnsByRefReadonly && !method.IsImplicitlyDeclared;

        private static bool HasAttribute(ISymbol symbol, string name) =>
            symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == name);

        /// <summary>[GeneratedMapper(RequireMapConversion = ..., Provider = ...)] and class-level [MapAllowParse].</summary>
        private void ReadClassSettings()
        {
            foreach (var attribute in _mapper.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                if (name == MapperGenerator.GeneratedMapperAttributeName)
                {
                    foreach (var argument in attribute.NamedArguments)
                    {
                        if (argument.Key == "RequireMapConversion" && argument.Value.Value is bool require) _requireMapConversion = require;
                        else if (argument.Key == "Provider" && argument.Value.Value is string provider)
                        {
                            var known = ProjectionProvider.Normalize(provider);
                            if (known != null) _provider = known;
                            else _diagnostics.Add(Diagnostic.Create(Diagnostics.UnknownProvider, AttributeLocation(attribute), provider, ProjectionProvider.KnownList));
                        }
                    }
                }
                else if (name == MapperGenerator.MapAllowParseAttributeName)
                {
                    foreach (var member in StringArguments(attribute)) _classAllowParse.Add(member);
                }
                else if (name == MapperGenerator.MapReviewedAttributeName)
                {
                    foreach (var member in StringArguments(attribute)) _classReviewed.Add(member);
                }
            }
        }

        private Location AttributeLocation(AttributeData attribute) =>
            attribute.ApplicationSyntaxReference?.GetSyntax(_ct).GetLocation() ?? _mapper.Locations.FirstOrDefault() ?? Location.None;

        /// <summary>The string values of an attribute's constructor arguments, including params arrays.</summary>
        private static IEnumerable<string> StringArguments(AttributeData attribute)
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Kind == TypedConstantKind.Array)
                {
                    foreach (var value in argument.Values)
                        if (value.Value is string item) yield return item;
                }
                else if (argument.Value is string single)
                {
                    yield return single;
                }
            }
        }

        private void RegisterUserMap(IMethodSymbol method, bool overwrite = false)
        {
            if (!IsConversionShape(method)) return;
            var key = PairKey(method.Parameters[0].Type, method.ReturnType);
            if (overwrite || !_userMaps.ContainsKey(key)) _userMaps[key] = method;
            if (overwrite) _conversionMethods[key] = method;
        }

        /// <summary>
        /// WriteSets: the destination members each mapping method sets (by "Name(ParameterTypes)", and by name alone when the
        /// method has no overloads), so tests can compare a map's real behavior with what the generator wrote.
        /// </summary>
        private void EmitWriteSets(List<IMethodSymbol> methods, CodeWriter w)
        {
            if (methods.Count == 0 || !_mapper.GetMembers("WriteSets").IsEmpty) return;
            var list = "global::System.Collections.Generic.IReadOnlyList<string>";
            w.Line();
            w.Line("/// <summary>The destination members each generated mapping method writes, by method (\"Name(ParameterTypes)\", or \"Name\" when it has no overloads).</summary>");
            w.Line("internal static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, " + list + "> WriteSets =");
            w.Indent();
            w.Line("new global::System.Collections.Generic.Dictionary<string, " + list + ">(global::System.StringComparer.Ordinal)");
            w.Line("{");
            w.Indent();
            var names = methods.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var method in methods)
            {
                var set = WriteSet(method);
                var members = set.Count == 0
                    ? "global::System.Array.Empty<string>()"
                    : "new string[] { " + string.Join(", ", set.Select(m => SymbolDisplay.FormatLiteral(m, true))) + " }";
                w.Line("[" + SymbolDisplay.FormatLiteral(MethodDisplay(method), true) + "] = " + members + ",");
                if (names[method.Name] == 1) w.Line("[" + SymbolDisplay.FormatLiteral(method.Name, true) + "] = " + members + ",");
            }
            w.Outdent();
            w.Line("};");
            w.Outdent();
            EmitWriteSetOf(methods, w);
            EmitLineage(methods, w);
        }

        /// <summary>
        /// Lineage: for each mapping method, destination member -> the source member path it is copied from (plain copies,
        /// including flattening and built-in conversions; not parameters, constants or conversion methods). MapperAssert.WritesFrom
        /// checks real behavior against it.
        /// </summary>
        private void EmitLineage(List<IMethodSymbol> methods, CodeWriter w)
        {
            if (!_mapper.GetMembers("Lineage").IsEmpty) return;
            var inner = "global::System.Collections.Generic.IReadOnlyDictionary<string, string>";
            w.Line();
            w.Line("/// <summary>For each generated mapping method (keys as in WriteSets): destination member -> the source member path it is copied from.</summary>");
            w.Line("internal static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, " + inner + "> Lineage =");
            w.Indent();
            w.Line("new global::System.Collections.Generic.Dictionary<string, " + inner + ">(global::System.StringComparer.Ordinal)");
            w.Line("{");
            w.Indent();
            var names = methods.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var method in methods)
            {
                var entries = _lineage.TryGetValue(method, out var map) ? map : new SortedDictionary<string, string>(StringComparer.Ordinal);
                var value = "new global::System.Collections.Generic.Dictionary<string, string>(global::System.StringComparer.Ordinal) { " +
                            string.Join(", ", entries.Select(e => "[" + SymbolDisplay.FormatLiteral(e.Key, true) + "] = " + SymbolDisplay.FormatLiteral(e.Value, true))) + " }";
                w.Line("[" + SymbolDisplay.FormatLiteral(MethodDisplay(method), true) + "] = " + value + ",");
                if (names[method.Name] == 1) w.Line("[" + SymbolDisplay.FormatLiteral(method.Name, true) + "] = " + value + ",");
            }
            w.Outdent();
            w.Line("};");
            w.Outdent();
        }

        /// <summary>
        /// WriteSetOf(nameof(M), typeof(A), ...): the write set by method name and parameter types, so a renamed method is a
        /// compile error and a changed signature throws (listing the current overloads) instead of a silently missing key.
        /// </summary>
        private void EmitWriteSetOf(List<IMethodSymbol> methods, CodeWriter w)
        {
            if (!_mapper.GetMembers("WriteSetOf").IsEmpty) return;
            var list = "global::System.Collections.Generic.IReadOnlyList<string>";
            w.Line();
            w.Line("/// <summary>");
            w.Line("/// The destination members the generated method <paramref name=\"method\"/> writes. Pass its parameter types when it has overloads:");
            w.Line("/// <c>WriteSetOf(nameof(ToDto), typeof(Order), typeof(string))</c>. Throws when no generated method matches.");
            w.Line("/// </summary>");
            w.Open("internal static " + list + " WriteSetOf(string method, params global::System.Type[] parameterTypes)");
            w.Line("var candidates = 0;");
            foreach (var method in methods)
            {
                var types = method.Parameters.Select(p => "typeof(" + T(TypeofType(p.Type)) + ")").ToList();
                w.Open("if (method == " + SymbolDisplay.FormatLiteral(method.Name, true) + ")");
                w.Line("candidates++;");
                w.Line("if (parameterTypes.Length == 0 && WriteSets.ContainsKey(method)) return WriteSets[method];");
                var condition = "parameterTypes.Length == " + types.Count.ToString(CultureInfo.InvariantCulture) +
                                string.Concat(types.Select((t, i) => " && parameterTypes[" + i.ToString(CultureInfo.InvariantCulture) + "] == " + t));
                w.Line("if (" + condition + ") return WriteSets[" + SymbolDisplay.FormatLiteral(MethodDisplay(method), true) + "];");
                w.Close();
            }
            w.Line("throw new global::System.ArgumentException(candidates == 0");
            w.Indent();
            w.Line("? \"No generated mapping method is named '\" + method + \"'.\"");
            w.Line(": \"No generated overload of '\" + method + \"' has these parameter types. Overloads: \" + string.Join(\", \", global::System.Linq.Enumerable.Where(WriteSets.Keys, k => k.StartsWith(method + \"(\", global::System.StringComparison.Ordinal))), nameof(method));");
            w.Outdent();
            w.Close();
        }

        /// <summary>The runtime type of a parameter, as typeof sees it: no nullable reference annotation.</summary>
        private static ITypeSymbol TypeofType(ITypeSymbol type) =>
            type.IsReferenceType ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : type;

        /// <summary>CM0017 on every ordinary static method that was used as a conversion without [MapConversion].</summary>
        private void ReportUnmarkedConversions()
        {
            foreach (var pair in _conversionUses.OrderBy(p => p.Key.Name, StringComparer.Ordinal))
            {
                var method = pair.Key;
                if (_markedConversions.Contains(method) || IsPartialDefinition(method) || pair.Value.Count == 0) continue;
                var members = pair.Value.Take(3).Select(m => "'" + m + "'").ToList();
                var more = pair.Value.Count > 3 ? " and " + (pair.Value.Count - 3).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty;
                _diagnostics.Add(Diagnostic.Create(Diagnostics.UnmarkedConversionMethod,
                    method.Locations.FirstOrDefault() ?? Location.None,
                    method.Name, Short(method.Parameters[0].Type) + " to " + Short(method.ReturnType), string.Join(", ", members) + more));
            }
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

        private void EmitMethod(IMethodSymbol method, MethodDeclarationSyntax syntax, CodeWriter output)
        {
            _location = syntax.Identifier.GetLocation();
            _current = method;
            _rootSource = _rootDestination = null;
            if (TryGetMapTypes(method, out var rootSource, out var rootDestination))
            {
                _rootSource = MemberResolver.IsNullableValueType(rootSource, out var underlying) ? underlying : rootSource;
                _rootDestination = rootDestination;
                if (IsObjectLike(_rootSource, true)) _rootSources.Add(_rootSource.OriginalDefinition);
            }
            WriteSet(method);

            ReportNameofCandidates(method);
            var w = new CodeWriter(_memberIndent);
            w.Open(Signature(method, syntax));
            EmitMethodBody(method, w);
            w.Close();
            FlushUnmapped(method);

            // Optional XML docs listing where each member comes from (MSBuild: ConventionMapperGeneratedDocs=true).
            foreach (var line in MethodDocs(method, syntax)) output.Line(DocMarker + line);
            output.Raw(w.ToString());
            _rootSource = _rootDestination = null;
        }

        // ------------------------------------------------------------------ [MapRedact], [MapLogString], Diff

        /// <summary>The masked value of a [Sensitive] member; nested objects and collections are redacted by helpers.</summary>
        private string RedactValue(ISymbol member, ITypeSymbol type, string value)
        {
            if (MemberResolver.IsSensitive(member))
                return type.SpecialType == SpecialType.System_String ? "\"***\"" : "default(" + T(type) + ")!";
            if (IsObjectLike(type, false) && ContainsSensitive(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
                return RedactHelper((INamedTypeSymbol)type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)) + "(" + value + ")";
            var element = ElementTypeOf(type);
            var kind = GetCollectionKind(type);
            if (element != null && (kind == CollectionKind.List || kind == CollectionKind.Array) && IsObjectLike(element, false) &&
                ContainsSensitive(element, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
            {
                var helper = RedactHelper((INamedTypeSymbol)element.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
                var v = NewVariable();
                var items = "global::System.Linq.Enumerable.Select(" + v + ", " + helper + ")";
                var collected = kind == CollectionKind.Array ? "global::System.Linq.Enumerable.ToArray(" + items + ")" : "global::System.Linq.Enumerable.ToList(" + items + ")";
                return "(" + value + " is { } " + v + " ? " + collected + " : null!)";
            }
            return value;
        }

        private bool ContainsSensitive(ITypeSymbol type, HashSet<ITypeSymbol> visited)
        {
            if (!visited.Add(type)) return false;
            foreach (var member in _resolver.GetReadableMembers(type))
            {
                if (member is IMethodSymbol) continue;
                if (MemberResolver.IsSensitive(member)) return true;
                var memberType = MemberResolver.TypeOf(member);
                var target = ElementTypeOf(memberType) ?? memberType;
                if (IsObjectLike(target, false) && ContainsSensitive(target, visited)) return true;
            }
            return false;
        }

        private string RedactHelper(INamedTypeSymbol type)
        {
            var key = "redact:" + T(type);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = "__Redact" + (++_counter).ToString(CultureInfo.InvariantCulture) + "_" + Identifier(type);
            _helperNames[key] = name;
            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Open("private static " + T(type) + " " + name + "(" + T(type) + " value)");
            EmitCreateBody(w, type, type, "value", new MemberConfig { Redact = true });
            w.Close();
            _helpers.Append(w.ToString());
            return name;
        }

        /// <summary>string ToLogString(T value) with [MapLogString]: "T { A = 1, B = "x", Ssn = *** }".</summary>
        private void EmitLogString(IMethodSymbol method, CodeWriter w)
        {
            if (method.Parameters.Length != 1 || method.ReturnType.SpecialType != SpecialType.System_String)
            {
                Report(Diagnostics.InvalidValueAttribute, "MapLogString", method.Name, method.Name, "a log method must be string ToLogString(T value)");
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }
            var type = method.Parameters[0].Type;
            var value = Escape(method.Parameters[0].Name);
            w.Line("var sb = new global::System.Text.StringBuilder();");
            w.Line("_ = " + LogAppend(type, value, "0", null) + ";");
            w.Line("return sb.ToString();");
        }

        /// <summary>An expression that appends <paramref name="value"/> to "sb" (used as "_ = ...;").</summary>
        private string LogAppend(ITypeSymbol type, string value, string depth, ISymbol? member)
        {
            if (member != null && MemberResolver.IsSensitive(member)) return "sb.Append(\"***\")";
            if (type.SpecialType == SpecialType.System_String)
                return "(" + value + " is null ? sb.Append(\"null\") : sb.Append('\"').Append(" + value + ").Append('\"'))";
            var plain = MemberResolver.IsNullableValueType(type, out var underlying) ? underlying : type;
            if (IsObjectLike(plain, true) && !(plain is IArrayTypeSymbol))
                return "sb.Append(" + LogHelper(plain.WithNullableAnnotation(NullableAnnotation.NotAnnotated)) + "(" + value + ", " + depth + "))";
            if (ElementTypeOf(type) != null)
                return "(" + value + " is null ? sb.Append(\"null\") : sb.Append('[').Append(global::System.Linq.Enumerable.Count(" + value + ")).Append(\" items]\"))";
            return LogScalarHelper() + "(sb, " + value + ")";
        }

        private string LogHelper(ITypeSymbol type)
        {
            var key = "log:" + T(type);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = "__Log" + (++_counter).ToString(CultureInfo.InvariantCulture) + "_" + Identifier(type);
            _helperNames[key] = name;
            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Open("private static string " + name + "(" + T(type) + "? value, int depth)");
            w.Line("if (value is null) return \"null\";");
            w.Line("if (depth > 3) return \"" + Short(type) + " { ... }\";");
            w.Line("var sb = new global::System.Text.StringBuilder();");
            w.Line("sb.Append(\"" + Short(type) + " { \");");
            var first = true;
            foreach (var member in _resolver.GetReadableMembers(type))
            {
                if (member is IMethodSymbol) continue;
                w.Line((first ? string.Empty : "sb.Append(\", \"); ") + "sb.Append(\"" + member.Name + " = \");");
                first = false;
                var access = "value." + Escape(member.Name);
                w.Line("_ = " + LogAppend(MemberResolver.TypeOf(member), access, "depth + 1", member) + ";");
            }
            w.Line("sb.Append(\" }\");");
            w.Line("return sb.ToString();");
            w.Close();
            _helpers.Append(w.ToString());
            return name;
        }

        private string LogScalarHelper()
        {
            const string key = "helper:logscalar";
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = "__LogScalar";
            _helperNames[key] = name;
            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Line("private static global::System.Text.StringBuilder " + name + "(global::System.Text.StringBuilder sb, object? value) =>");
            w.Indent();
            w.Line("value is null ? sb.Append(\"null\") : value is global::System.IFormattable f ? sb.Append(f.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture)) : sb.Append(value);");
            w.Outdent();
            _helpers.Append(w.ToString());
            return name;
        }

        /// <summary>
        /// IReadOnlyList&lt;FieldChange&gt; Diff(TBefore before, TAfter after), or, for public methods (FieldChange is internal),
        /// IReadOnlyList&lt;(string Member, object? Before, object? After)&gt;.
        /// </summary>
        private static bool IsDiffMethod(IMethodSymbol method) =>
            method.Parameters.Length == 2 && method.ReturnType is INamedTypeSymbol list && list.IsGenericType &&
            list.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IReadOnlyList<T>" &&
            (list.TypeArguments[0].ToDisplayString() == MapperGenerator.FieldChangeName || IsChangeTuple(list.TypeArguments[0]));

        private static bool IsChangeTuple(ITypeSymbol type) =>
            type is INamedTypeSymbol tuple && tuple.IsTupleType && tuple.TupleElements.Length == 3 &&
            tuple.TupleElements[0].Type.SpecialType == SpecialType.System_String &&
            tuple.TupleElements[1].Type.SpecialType == SpecialType.System_Object && tuple.TupleElements[2].Type.SpecialType == SpecialType.System_Object;

        /// <summary>
        /// Compares the members the conventions match (the same matching as a map from TBefore to TAfter, with [MapMember] and
        /// [MapIgnore]); [Sensitive] values are reported as "***".
        /// </summary>
        private void EmitDiff(IMethodSymbol method, CodeWriter w)
        {
            var config = ReadConfig(method);
            var before = method.Parameters[0].Type;
            var after = method.Parameters[1].Type;
            var b = Escape(method.Parameters[0].Name);
            var a = Escape(method.Parameters[1].Name);
            var element = ((INamedTypeSymbol)method.ReturnType).TypeArguments[0];
            var isTuple = IsChangeTuple(element);
            var change = isTuple ? element.ToDisplayString(SignatureFormat) : "global::" + MapperGenerator.FieldChangeName;
            string Create(string member, string oldValue, string newValue) =>
                isTuple ? "(" + member + ", (object?)(" + oldValue + "), (object?)(" + newValue + "))" : "new " + change + "(" + member + ", " + oldValue + ", " + newValue + ")";
            w.Line("var changes = new global::System.Collections.Generic.List<" + change + ">();");
            w.Open("if (" + b + " is null || " + a + " is null)");
            w.Line("if (!ReferenceEquals(" + b + ", " + a + ")) changes.Add(" + Create("\"(object)\"", b, a) + ");");
            w.Line("return changes;");
            w.Close();
            if (!IsObjectLike(before, true) || !IsObjectLike(after, true))
            {
                Report(Diagnostics.UnsupportedSignature, method.Name);
                w.Line("return changes;");
                return;
            }
            RecordMap(before, after);
            foreach (var member in _resolver.GetReadableMembers(after))
            {
                if (member is IMethodSymbol || config.Ignored.Contains(member.Name) || !config.Includes(member.Name)) continue;
                var path = config.Explicit.TryGetValue(member.Name, out var explicitPath)
                    ? _resolver.FindExplicitPath(before, explicitPath)
                    : FindConventionPath(before, member.Name, after, out _);
                if (path == null) continue; // members only on one side are not compared
                var oldAccess = BuildPath(b, before, path);
                var newType = MemberResolver.TypeOf(member);
                var oldValue = Convert(oldAccess.Type, newType, oldAccess.Expression, 0);
                if (oldValue == null) continue;
                var sensitive = MemberResolver.IsSensitive(member) || path.Any(MemberResolver.IsSensitive);
                var o = NewVariable();
                var n = NewVariable();
                w.Open(null);
                w.Line("var " + o + " = " + oldValue + ";");
                w.Line("var " + n + " = " + a + "." + Escape(member.Name) + ";");
                var equal = ElementTypeOf(newType) != null && newType.SpecialType != SpecialType.System_String
                    ? "(ReferenceEquals(" + o + ", " + n + ") || (" + o + " is not null && " + n + " is not null && global::System.Linq.Enumerable.SequenceEqual(" + o + ", " + n + ")))"
                    : "global::System.Collections.Generic.EqualityComparer<" + T(newType) + ">.Default.Equals(" + o + ", " + n + ")";
                w.Line("if (!" + equal + ") changes.Add(" + Create("\"" + member.Name + "\"", sensitive ? "\"***\"" : o, sensitive ? "\"***\"" : n) + ");");
                w.Close();
                RecordFlow(before, after, member.Name, member, path, new List<string> { "compared" + (sensitive ? " (values masked)" : string.Empty) });
            }
            w.Line("return changes;");
        }

        // ------------------------------------------------------------------ [MapMergeKey] collection merges

        /// <summary>
        /// void Merge(IEnumerable&lt;S&gt; source, ICollection&lt;D&gt; target): update matched items, add new ones, remove missing ones.
        /// </summary>
        private void EmitMerge(IMethodSymbol method, AttributeData attribute, CodeWriter w)
        {
            var location = AttributeLocation(attribute);
            var sourceElement = method.Parameters.Length == 2 ? ElementTypeOf(method.Parameters[0].Type) : null;
            var targetType = method.Parameters.Length == 2 ? method.Parameters[1].Type as INamedTypeSymbol : null;
            var targetCollection = targetType == null ? null :
                targetType.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_ICollection_T ? targetType :
                targetType.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_ICollection_T);
            if (!method.ReturnsVoid || sourceElement == null || targetCollection == null)
            {
                ReportAt(Diagnostics.InvalidValueAttribute, location, "MapMergeKey", method.Name, method.Name,
                    "a merge method must be void Merge(IEnumerable<TSource> source, ICollection<TTarget> target)");
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }
            var targetElement = targetCollection.TypeArguments[0];
            // The element pair is this method's root in the report and the write sets.
            _rootSource = sourceElement;
            _rootDestination = targetElement;
            if (IsObjectLike(sourceElement, true)) _rootSources.Add(sourceElement.OriginalDefinition);
            var sourceKeyName = attribute.ConstructorArguments[0].Value as string ?? string.Empty;
            var targetKeyName = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is string t ? t : sourceKeyName;
            var removeMissing = !attribute.NamedArguments.Any(a => a.Key == "RemoveMissing" && a.Value.Value is bool b && !b);

            var sourceKey = _resolver.FindExplicitPath(sourceElement, sourceKeyName);
            var targetKey = _resolver.FindExplicitPath(targetElement, targetKeyName);
            if (sourceKey == null || targetKey == null)
            {
                ReportAt(Diagnostics.InvalidValueAttribute, location, "MapMergeKey", sourceKey == null ? sourceKeyName : targetKeyName, method.Name,
                    "no member '" + (sourceKey == null ? sourceKeyName : targetKeyName) + "' on " + Short(sourceKey == null ? sourceElement : targetElement));
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }
            var keyType = MemberResolver.TypeOf(targetKey[targetKey.Count - 1]);
            if (keyType.IsReferenceType) keyType = keyType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            var sourceKeyAccess = BuildPath("item", sourceElement, sourceKey);
            // Keys must be the same type or convert implicitly (no parsing: "1" and 1 are not the same key).
            var plainSourceKey = MemberResolver.IsNullableValueType(sourceKeyAccess.Type, out var su) ? su : sourceKeyAccess.Type;
            var plainKey = MemberResolver.IsNullableValueType(keyType, out var ku) ? ku : keyType;
            if (!SymbolEqualityComparer.Default.Equals(plainSourceKey, plainKey) && !_compilation.ClassifyConversion(plainSourceKey, plainKey).IsImplicit)
            {
                ReportAt(Diagnostics.InvalidValueAttribute, location, "MapMergeKey", sourceKeyName, method.Name,
                    "the keys have different types (" + Short(plainSourceKey) + " and " + Short(plainKey) + ")");
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }
            var sourceKeyValue = plainKey.IsValueType ? "(" + T(plainKey) + "?)(" + sourceKeyAccess.Expression + ")" : sourceKeyAccess.Expression;

            // Updates use the mapper's Update(S, D) when it declares one (with its configuration), otherwise a generated helper.
            var update = _mapper.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.ReturnsVoid && m.Parameters.Length == 2 && !m.IsGenericMethod &&
                SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, sourceElement) && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, targetElement) &&
                !m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == MapperGenerator.MapMergeKeyAttributeName));
            var updateCall = update != null ? Escape(update.Name) : UpdateHelper(sourceElement, (INamedTypeSymbol)targetElement);
            var create = Convert(sourceElement, targetElement, "item", 0);
            if (create == null)
            {
                ReportAt(Diagnostics.InvalidValueAttribute, location, "MapMergeKey", method.Name, method.Name,
                    "cannot create " + Short(targetElement) + " from " + Short(sourceElement));
                w.Line("throw new global::System.NotSupportedException();");
                return;
            }

            var key = T(MemberResolver.IsNullableValueType(keyType, out var underlyingKey) ? underlyingKey : keyType);
            var src = Escape(method.Parameters[0].Name);
            var dest = Escape(method.Parameters[1].Name);
            var targetKeyAccess = BuildPath("existing", targetElement, targetKey);
            w.Line("if (" + src + " is null) return;");
            w.Line("if (" + dest + " is null) throw new global::System.ArgumentNullException(nameof(" + dest + "));");
            w.Line("var byKey = new global::System.Collections.Generic.Dictionary<" + key + ", " + T(targetElement) + ">();");
            w.Open("foreach (var existing in " + dest + ")");
            w.Line("if (existing is null) continue;");
            w.Line("var existingKey = " + targetKeyAccess.Expression + ";");
            w.Line("if (existingKey is " + key + " k && !byKey.ContainsKey(k)) byKey.Add(k, existing);");
            w.Close();
            w.Line("var matched = new global::System.Collections.Generic.HashSet<" + key + ">();");
            w.Line("var added = new global::System.Collections.Generic.List<" + T(targetElement) + ">();");
            w.Open("foreach (var item in " + src + ")");
            w.Line("if (item is null) continue;");
            w.Line("var sourceKey = " + sourceKeyValue + ";");
            w.Open("if (sourceKey is " + key + " sk && byKey.TryGetValue(sk, out var match))");
            w.Line(updateCall + "(item, match);");
            w.Line("matched.Add(sk);");
            w.Close();
            w.Open("else");
            w.Line("added.Add(" + create + ");");
            w.Close();
            w.Close();
            if (removeMissing)
            {
                w.Line("foreach (var pair in byKey)");
                w.Line("    if (!matched.Contains(pair.Key)) " + dest + ".Remove(pair.Value);");
            }
            w.Line("foreach (var item in added) " + dest + ".Add(item);");

            var section = RecordMap(sourceElement, targetElement);
            section.Rows["(merge)"] = "| (merge) | `" + sourceKeyName + "` = `" + targetKeyName + "` | update matched" +
                                       (update != null ? " with `" + update.Name + "()`" : string.Empty) + ", add new" + (removeMissing ? ", remove missing" : ", keep missing") + " |";
        }

        /// <summary>A generated update helper for an element pair that has no declared Update method.</summary>
        private string UpdateHelper(ITypeSymbol source, INamedTypeSymbol target)
        {
            var key = "update:" + PairKey(source, target);
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = NewHelperName("Update", source, target);
            _helperNames[key] = name;
            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Open("private static void " + name + "(" + T(source) + " source, " + T(target) + " target)");
            EmitUpdateBody(w, source, target, "source", "target", MemberConfig.Empty, name);
            w.Close();
            _helpers.Append(w.ToString());
            return name;
        }

        // ------------------------------------------------------------------ diagnostics with IDE fixes

        private readonly List<KeyValuePair<string, object[]>> _pendingUnmapped = new List<KeyValuePair<string, object[]>>();
        private bool _pendingExplicitMode;

        /// <summary>
        /// CM0001. For the method's own destination the diagnostic is created after the method is generated, with the
        /// properties the IDE fixes need: the member, whether the method is in explicit mode, and the members it writes.
        /// </summary>
        private void ReportUnmapped(string member, ITypeSymbol owner, ITypeSymbol source, MemberConfig config)
        {
            var args = new object[] { member, Short(owner), Short(source) };
            var isRoot = _rootDestination != null && _rootSource != null &&
                         SymbolEqualityComparer.Default.Equals(owner, _rootDestination) && SymbolEqualityComparer.Default.Equals(source, _rootSource);
            if (!isRoot)
            {
                Report(Diagnostics.UnmappedMember, args);
                return;
            }
            _pendingExplicitMode = config.ExplicitMode;
            _pendingUnmapped.Add(new KeyValuePair<string, object[]>(member, args));
        }

        private void FlushUnmapped(IMethodSymbol method)
        {
            if (_pendingUnmapped.Count == 0) return;
            var mapped = string.Join(",", WriteSet(method));
            foreach (var pending in _pendingUnmapped)
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("Member", pending.Key)
                    .Add("ExplicitMode", _pendingExplicitMode ? "true" : "false")
                    .Add("Mapped", mapped);
                _diagnostics.Add(Diagnostic.Create(Diagnostics.UnmappedMember, _location, properties, pending.Value));
            }
            _pendingUnmapped.Clear();
        }

        /// <summary>CM0020 (hidden): a string in a mapping attribute that names a destination member and could be nameof(...).</summary>
        private void ReportNameofCandidates(IMethodSymbol method)
        {
            if (!TryGetMapTypes(method, out _, out var destinationType) || !(destinationType is INamedTypeSymbol destination) ||
                destination.IsGenericType || destination.IsAnonymousType || !IsAccessible(destination)) return;
            var members = GetWritableMembers(destination, false);
            foreach (var attribute in method.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                int[]? positions;
                if (name == MapperGenerator.MapExplicitAttributeName || name == MapperGenerator.MapAllowParseAttributeName ||
                    name == MapperGenerator.MapReviewedAttributeName) positions = null; // all
                else if (name == MapperGenerator.MapIgnoreAttributeName || name == MapperGenerator.MapNullSubstituteAttributeName) positions = new[] { 0 };
                else if (name == MapperGenerator.MapMemberAttributeName) positions = new[] { 1 };
                else continue;

                if (!(attribute.ApplicationSyntaxReference?.GetSyntax(_ct) is AttributeSyntax syntax) || syntax.ArgumentList == null) continue;
                var arguments = syntax.ArgumentList.Arguments;
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (positions != null && Array.IndexOf(positions, i) < 0) continue;
                    if (arguments[i].NameEquals != null || arguments[i].NameColon != null) continue;
                    if (!(arguments[i].Expression is LiteralExpressionSyntax literal) || !literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                    var text = literal.Token.ValueText;
                    var member = members.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.Ordinal)) ??
                                 members.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase));
                    if (member == null || !IsAccessible(member)) continue;
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add("Type", T(destination))
                        .Add("Member", member.Name);
                    _diagnostics.Add(Diagnostic.Create(Diagnostics.UseNameof, literal.GetLocation(), properties,
                        Short(destination) + "." + member.Name, text));
                }
            }
        }

        /// <summary>Doc lines carry this prefix; the generator turns them into "///" comments or drops them, per the MSBuild option.</summary>
        internal const string DocMarker = "//#cm-doc ";

        /// <summary>
        /// Docs for the implementation part. The implementation's docs replace the declaration's in the compiler output,
        /// so the declaration's own comment is carried over, and the data flow goes into &lt;remarks&gt; (skipped when the
        /// declaration already has remarks).
        /// </summary>
        private IEnumerable<string> MethodDocs(IMethodSymbol method, MethodDeclarationSyntax syntax)
        {
            var section = _flows.Values.FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.Method, method));
            var own = DeclarationDocs(syntax);
            if (section == null)
            {
                foreach (var line in own) yield return line;
                yield break;
            }

            var element = own.Count == 0 ? "summary" : "remarks";
            foreach (var line in own) yield return line;
            if (own.Any(l => l.IndexOf("<remarks", StringComparison.Ordinal) >= 0)) yield break;

            yield return "<" + element + ">";
            yield return "Generated by ConventionMapper. Data flow:";
            yield return "<list type=\"table\">";
            foreach (var row in section.Rows.Values)
            {
                // "| Destination | Source | Notes |" (Notes may be empty)
                var inner = row.StartsWith("| ", StringComparison.Ordinal) && row.EndsWith(" |", StringComparison.Ordinal) ? row.Substring(2, row.Length - 4) : row;
                var cells = inner.Split(new[] { " | " }, StringSplitOptions.None);
                if (cells.Length < 3) continue;
                var notes = cells[2].Trim();
                yield return "<item><term>" + Xml(cells[0].Trim()) + "</term><description>" + Xml(cells[1].Trim().Trim('`')) +
                             (notes.Length > 0 ? " (" + Xml(notes) + ")" : string.Empty) + "</description></item>";
            }
            yield return "</list>";
            yield return "</" + element + ">";
        }

        /// <summary>The lines of the declaration's "///" comment, without the slashes.</summary>
        private static List<string> DeclarationDocs(MethodDeclarationSyntax syntax)
        {
            var lines = new List<string>();
            foreach (var trivia in syntax.GetLeadingTrivia())
            {
                // Without documentation parsing (DocumentationMode.None) "///" lines are ordinary comments.
                if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;
                foreach (var raw in trivia.ToFullString().Split('\n'))
                {
                    var line = raw.TrimEnd('\r').TrimStart();
                    if (!line.StartsWith("///", StringComparison.Ordinal)) continue;
                    line = line.Substring(3);
                    if (line.StartsWith(" ", StringComparison.Ordinal)) line = line.Substring(1);
                    lines.Add(line);
                }
            }
            return lines;
        }

        private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private void EmitMethodBody(IMethodSymbol method, CodeWriter w)
        {
            if (!method.IsGenericMethod && TryGetProjection(method, out var projectedSource, out var projectedDestination, out var isQueryable))
            {
                EmitProjection(w, method, projectedSource, projectedDestination, isQueryable);
                return;
            }

            if (IsDiffMethod(method))
            {
                EmitDiff(method, w);
                return;
            }
            if (HasAttribute(method, MapperGenerator.MapLogStringAttributeName))
            {
                EmitLogString(method, w);
                return;
            }

            var mergeKey = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MapperGenerator.MapMergeKeyAttributeName);
            if (mergeKey != null)
            {
                EmitMerge(method, mergeKey, w);
                return;
            }

            var isCreate = method.Parameters.Length >= 1 && !method.ReturnsVoid;
            var isUpdate = method.Parameters.Length >= 2 && method.ReturnsVoid;
            if (method.IsGenericMethod || method.ReturnsByRef || method.ReturnsByRefReadonly ||
                method.Parameters.Any(p => p.RefKind != RefKind.None) || !(isCreate || isUpdate) ||
                (isUpdate && method.Parameters[1].Type.IsValueType))
            {
                Report(Diagnostics.UnsupportedSignature, method.Name);
                w.Line("throw new global::System.NotSupportedException(\"Unsupported mapping method signature.\");");
                return;
            }

            var config = ReadConfig(method);
            var source = method.Parameters[0];
            if (HasAttribute(method, MapperGenerator.MapRedactAttributeName))
            {
                if (!isCreate || !SymbolEqualityComparer.Default.Equals(source.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), method.ReturnType.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
                    Report(Diagnostics.InvalidValueAttribute, "MapRedact", method.Name, method.Name, "a redact method must be T Redact(T value)");
                else config.Redact = true;
            }
            // Extra parameters supply values for the destination members with the same name (or as [MapMember] sources).
            foreach (var extra in method.Parameters.Skip(isCreate ? 1 : 2))
            {
                config.Parameters[extra.Name] = extra;
                // An overload that takes the value overrides an inherited [MapIgnore] for it.
                if (!config.OwnIgnored.Contains(extra.Name)) config.Ignored.Remove(extra.Name);
            }
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
            foreach (var extra in config.Parameters.Values)
                if (!config.UsedParameters.Contains(extra.Name)) Report(Diagnostics.UnusedParameter, extra.Name, method.Name);
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

        private MemberConfig ReadConfig(IMethodSymbol method)
        {
            var config = new MemberConfig();
            foreach (var attribute in _mapper.GetAttributes()) ReadExplicit(attribute, config);
            ReadMethodConfig(method, config, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method });
            _currentReviewed = config.Reviewed;
            return config;
        }

        /// <summary>Applies the inherited method's configuration first ([MapInherit]), then this method's own attributes over it.</summary>
        private void ReadMethodConfig(IMethodSymbol method, MemberConfig config, HashSet<IMethodSymbol> chain)
        {
            var own = chain.Count == 1; // the method being generated, not one it inherits from
            var attributes = method.GetAttributes();
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass?.ToDisplayString() != MapperGenerator.MapInheritAttributeName) continue;
                if (attribute.ConstructorArguments.Length < 1 || !(attribute.ConstructorArguments[0].Value is string inheritedName)) continue;
                var inherited = FindInheritedMethod(method, inheritedName, attribute);
                if (inherited == null) continue;
                if (!chain.Add(inherited))
                {
                    ReportAt(Diagnostics.InvalidMapInherit, AttributeLocation(attribute), inheritedName, method.Name, "the methods inherit from each other in a cycle");
                    continue;
                }
                ReadMethodConfig(inherited, config, chain);
                chain.Remove(inherited);
            }

            foreach (var attribute in attributes)
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                var args = attribute.ConstructorArguments;
                if (name == MapperGenerator.MapIgnoreAttributeName && args.Length == 1 && args[0].Value is string ignored)
                {
                    config.Ignored.Add(ignored);
                    if (own) config.OwnIgnored.Add(ignored);
                }
                else if (name == MapperGenerator.MapMemberAttributeName && args.Length == 2 && args[0].Value is string from && args[1].Value is string to)
                {
                    // "source.CreateUser" (the method's own source parameter name first) always means the source object,
                    // never a parameter, so parameters can keep natural names.
                    if (TryStripSourcePrefix(method, from, out var stripped))
                    {
                        config.Explicit[to] = stripped;
                        config.SourcePrefixed.Add(to);
                    }
                    else
                    {
                        config.Explicit[to] = from;
                        config.SourcePrefixed.Remove(to);
                    }
                }
                else if (name == MapperGenerator.MapNullSubstituteAttributeName && args.Length == 2 && args[0].Value is string target)
                    config.NullSubstitutes[target] = args[1];
                else if (name == MapperGenerator.MapAllowParseAttributeName)
                    foreach (var allowed in StringArguments(attribute)) config.AllowParse.Add(allowed);
                else if (name == MapperGenerator.MapReviewedAttributeName)
                    foreach (var reviewed in StringArguments(attribute)) config.Reviewed.Add(reviewed);
                else if (name == MapperGenerator.MapConstantAttributeName && args.Length == 2 && args[0].Value is string constantMember)
                {
                    config.Constants[constantMember] = args[1];
                    config.Computed.Remove(constantMember);
                }
                else if (name == MapperGenerator.MapFromAttributeName && args.Length == 2 && args[0].Value is string computeMethod && args[1].Value is string computedMember)
                {
                    config.Computed[computedMember] = computeMethod;
                    config.Constants.Remove(computedMember);
                }
                else if (name == MapperGenerator.MapRequiredAttributeName)
                    foreach (var required in StringArguments(attribute)) config.Required.Add(required);
                else if (name == MapperGenerator.MapTruncateAttributeName && args.Length >= 1 && args[0].Value is string truncated)
                    config.Truncate[truncated] = args.Length > 1 && args[1].Value is int length ? length : 0;
                else
                    ReadExplicit(attribute, config);
            }
        }

        /// <summary>"source.Customer.Name" -> "Customer.Name" when "source" is the method's source parameter (exact case) and not also a member of the source.</summary>
        private bool TryStripSourcePrefix(IMethodSymbol method, string path, out string stripped)
        {
            stripped = path;
            if (method.Parameters.Length == 0 || !TryGetMapTypes(method, out var source, out _)) return false;
            var prefix = method.Parameters[0].Name + ".";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) return false;
            if (MemberResolver.IsNullableValueType(source, out var underlying)) source = underlying;
            if (_resolver.GetReadableMembers(source).Any(m => string.Equals(m.Name, method.Parameters[0].Name, StringComparison.Ordinal))) return false;
            stripped = path.Substring(prefix.Length);
            return true;
        }

        /// <summary>
        /// The method named by [MapInherit]: another method of the mapper class with the same source and destination types.
        /// When several overloads qualify, [MapInherit(nameof(M), typeof(A), ...)] picks one by its parameter types;
        /// without them the choice would be a guess, so it is error CM0019.
        /// </summary>
        private IMethodSymbol? FindInheritedMethod(IMethodSymbol method, string name, AttributeData attribute)
        {
            var location = AttributeLocation(attribute);
            var candidates = _mapper.GetMembers(name).OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && !SymbolEqualityComparer.Default.Equals(m, method))
                .ToList();
            if (candidates.Count == 0)
            {
                ReportAt(Diagnostics.InvalidMapInherit, location, name, method.Name, "the mapper class has no other method named '" + name + "'");
                return null;
            }

            if (!TryGetMapTypes(method, out var source, out var destination))
                return null; // the method itself is reported as an unsupported signature

            var matching = candidates
                .Where(c => TryGetMapTypes(c, out var s, out var d) &&
                            SymbolEqualityComparer.Default.Equals(s, source) && SymbolEqualityComparer.Default.Equals(d, destination))
                .OrderBy(c => _methodOrder.TryGetValue(c, out var order) ? order : int.MaxValue)
                .ToList();
            if (matching.Count == 0)
            {
                var first = candidates[0];
                var detail = TryGetMapTypes(first, out var otherSource, out var otherDestination)
                    ? "'" + name + "' maps " + Short(otherSource) + " to " + Short(otherDestination) + " but '" + method.Name + "' maps " + Short(source) + " to " + Short(destination) + "; the source and destination types must be the same"
                    : "'" + name + "' is not a mapping method";
                ReportAt(Diagnostics.InvalidMapInherit, location, name, method.Name, detail);
                return null;
            }

            // [MapInherit(nameof(M), typeof(A), typeof(B))]: the overload with exactly these parameter types.
            var parameterTypes = attribute.ConstructorArguments.Skip(1)
                .SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : ImmutableArray.Create(a))
                .Select(a => a.Value as ITypeSymbol)
                .ToList();
            if (parameterTypes.Count > 0)
            {
                if (parameterTypes.Any(t => t == null)) return null; // unresolved typeof: the compiler reports it
                var exact = matching.FirstOrDefault(c => c.Parameters.Length == parameterTypes.Count &&
                                                         c.Parameters.Select(p => p.Type).SequenceEqual(parameterTypes!, SymbolEqualityComparer.Default));
                if (exact != null) return exact;
                ReportAt(Diagnostics.InvalidMapInherit, location, name, method.Name,
                    "no overload has the parameters (" + string.Join(", ", parameterTypes.Select(t => Short(t!))) + "); overloads: " + Overloads(matching));
                return null;
            }

            if (matching.Count == 1) return matching[0];
            ReportAt(Diagnostics.InvalidMapInherit, location, name, method.Name,
                "it matches " + matching.Count.ToString(CultureInfo.InvariantCulture) + " overloads (" + Overloads(matching) +
                "); choose one with its parameter types, for example [MapInherit(nameof(" + name + "), " +
                string.Join(", ", matching[0].Parameters.Select(p => "typeof(" + TypeofName(p.Type) + ")")) + ")]");
            return null;
        }

        private static string Overloads(IEnumerable<IMethodSymbol> methods) => string.Join(", ", methods.Select(MethodDisplay));

        /// <summary>The type as written in typeof(...): no nullable reference annotation.</summary>
        private static string TypeofName(ITypeSymbol type) =>
            (type.IsReferenceType ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : type).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        /// <summary>Source and destination types of a create, update or projection method.</summary>
        private static bool TryGetMapTypes(IMethodSymbol method, out ITypeSymbol source, out ITypeSymbol destination)
        {
            if (TryGetProjection(method, out source, out destination, out _)) return true;
            if (method.Parameters.Length >= 1 && !method.ReturnsVoid)
            {
                source = method.Parameters[0].Type;
                destination = method.ReturnType;
                return true;
            }
            if (method.Parameters.Length >= 2 && method.ReturnsVoid)
            {
                source = method.Parameters[0].Type;
                destination = method.Parameters[1].Type;
                return true;
            }
            source = destination = null!;
            return false;
        }

        private static void ReadExplicit(AttributeData attribute, MemberConfig config)
        {
            if (attribute.AttributeClass?.ToDisplayString() != MapperGenerator.MapExplicitAttributeName) return;
            config.ExplicitMode = true;
            foreach (var listed in StringArguments(attribute)) config.Listed.Add(listed);
        }

        private void CheckConfigUsage(MemberConfig config, IMethodSymbol method, ITypeSymbol destination)
        {
            if (config.Ignored.Count == 0 && config.Explicit.Count == 0 && config.Listed.Count == 0 && config.NullSubstitutes.Count == 0 && config.AllowParse.Count == 0 && config.Reviewed.Count == 0 &&
                config.Constants.Count == 0 && config.Computed.Count == 0 && config.Required.Count == 0 && config.Truncate.Count == 0) return;
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
            foreach (var listed in config.Listed)
                if (!known.Contains(listed)) Report(Diagnostics.UnknownAttributeMember, listed, "MapExplicit", method.Name);
            foreach (var pair in config.NullSubstitutes)
                if (!known.Contains(pair.Key)) Report(Diagnostics.UnknownAttributeMember, pair.Key, "MapNullSubstitute", method.Name);
            foreach (var allowed in config.AllowParse)
                if (!known.Contains(allowed)) Report(Diagnostics.UnknownAttributeMember, allowed, "MapAllowParse", method.Name);
            foreach (var reviewed in config.Reviewed)
                if (!known.Contains(reviewed)) Report(Diagnostics.UnknownAttributeMember, reviewed, "MapReviewed", method.Name);
            foreach (var pair in config.Constants)
                if (!known.Contains(pair.Key)) Report(Diagnostics.UnknownAttributeMember, pair.Key, "MapConstant", method.Name);
            foreach (var pair in config.Computed)
                if (!known.Contains(pair.Key)) Report(Diagnostics.UnknownAttributeMember, pair.Key, "MapFrom", method.Name);
            foreach (var required in config.Required)
                if (!known.Contains(required)) Report(Diagnostics.UnknownAttributeMember, required, "MapRequired", method.Name);
            foreach (var pair in config.Truncate)
                if (!known.Contains(pair.Key)) Report(Diagnostics.UnknownAttributeMember, pair.Key, "MapTruncate", method.Name);
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
                if (!config.Includes(member.Name))
                {
                    RecordSkipped(source, destination, member);
                    continue;
                }
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
                if (!config.Includes(member.Name))
                {
                    RecordSkipped(source, named, member);
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
                var chosenTraces = new List<Trace>();
                var chosenParameters = new List<string>();
                var ok = true;
                foreach (var p in ctor.Parameters)
                {
                    if (config.Ignored.Contains(p.Name) || !config.Includes(p.Name))
                    {
                        if (p.HasExplicitDefaultValue || p.IsParams) continue;
                        ok = false;
                        break;
                    }

                    string? value = null;
                    if (config.Constants.ContainsKey(p.Name) || config.Computed.ContainsKey(p.Name))
                    {
                        if (!TryAttributeValue(source, src, p.Name, p.Type, p, destination, config, false, out value, p.Name + " (constructor)") || value == null)
                        {
                            ok = false;
                            break;
                        }
                        args.Add(Escape(p.Name) + ": " + value);
                        used.Add(p.Name);
                        continue;
                    }
                    List<ISymbol>? path;
                    MemberAccess access = default;
                    var fromParameter = ResolveParameter(source, p.Name, destination, config, out var parameterPath, out var parameterAccess);
                    if (fromParameter == ParameterMatch.Invalid)
                    {
                        ok = false;
                        break;
                    }
                    if (fromParameter == ParameterMatch.Found)
                    {
                        path = parameterPath;
                        access = parameterAccess;
                    }
                    else
                    {
                        path = config.Explicit.TryGetValue(p.Name, out var explicitPath)
                            ? _resolver.FindExplicitPath(source, explicitPath)
                            : FindConventionPath(source, p.Name, destination, out _);
                        if (path != null) access = BuildPath(src, source, path);
                    }
                    if (path != null && path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(p) &&
                        !destination.GetMembers().Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && MemberResolver.IsSensitive(m)))
                    {
                        Report(Diagnostics.SensitiveLeak, Describe(source, path, fromParameter), Short(destination) + "(" + p.Name + ")", p.Name);
                        path = null;
                    }
                    var trace = new Trace();
                    if (path != null)
                    {
                        access = ApplyRequired(access, p.Name, destination, Describe(source, path, fromParameter), config, projection: false);
                        access = ApplyNullSubstitute(access, p.Name, p.Type, config);
                        var converted = access;
                        value = Traced(() => Convert(converted.Type, p.Type, converted.Expression, 0), out trace);
                        if (value != null) value = ApplyLength(value, path, p.Name, p.Type, destination, config, false, Describe(source, path, fromParameter));
                        if (value != null && config.Redact)
                            value = RedactValue(destination.GetMembers().FirstOrDefault(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && MemberResolver.IsSensitive(m)) ?? p, p.Type, value);
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
                    chosenTraces.Add(trace);
                    if (fromParameter == ParameterMatch.Found) chosenParameters.Add(path![0].Name);
                }
                if (!ok) continue;
                foreach (var name in chosenParameters) config.UsedParameters.Add(name);
                for (var i = 0; i < chosenPaths.Count; i++)
                {
                    var pair = chosenPaths[i];
                    var notes = CommitTrace(chosenTraces[i], Describe(source, pair.Value, pair.Value[0] is IParameterSymbol ? ParameterMatch.Found : ParameterMatch.None),
                        destination, pair.Key.Name, config, projection: false);
                    notes.Insert(0, pair.Value[0] is IParameterSymbol ? "constructor parameter, method parameter" : "constructor parameter");
                    RecordFlow(source, destination, pair.Key.Name + " (constructor)", pair.Key, pair.Value, notes);
                }
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
            if (TryAttributeValue(source, src, destinationName, destinationType, destinationMember, owner, config, projection: false, out var attributeValue))
                return attributeValue == null ? null : ApplyLength(attributeValue, null, destinationName, destinationType, owner, config, false, destinationName);
            List<ISymbol>? path;
            var fromParameter = ResolveParameter(source, destinationName, owner, config, out var parameterPath, out var parameterAccess);
            if (fromParameter == ParameterMatch.Invalid) return null;
            if (fromParameter == ParameterMatch.Found)
            {
                path = parameterPath;
            }
            else if (config.Explicit.TryGetValue(destinationName, out var explicitPath))
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
                path = FindConventionPath(source, destinationName, owner, out var ambiguous);
                if (path == null)
                {
                    if (!ambiguous) ReportUnmapped(destinationName, owner, source, config);
                    return null;
                }
            }

            var sourceName = Describe(source, path, fromParameter);
            if (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(destinationMember))
            {
                Report(Diagnostics.SensitiveLeak, sourceName, Short(owner) + "." + destinationName, destinationName);
                return null;
            }

            var access = fromParameter == ParameterMatch.Found ? parameterAccess : BuildPath(src, source, path);
            access = ApplyRequired(access, destinationName, owner, sourceName, config, projection: false);
            access = ApplyNullSubstitute(access, destinationName, destinationType, config);
            var value = Traced(() => Convert(access.Type, destinationType, access.Expression, 0), out var trace);
            if (value == null)
            {
                Report(Diagnostics.IncompatibleTypes, sourceName, Short(access.Type), Short(owner) + "." + destinationName, Short(destinationType), destinationName);
                return null;
            }
            value = ApplyLength(value, path, destinationName, destinationType, owner, config, false, sourceName);
            if (config.Redact) value = RedactValue(destinationMember, destinationType, value);
            if (access.MayBeNull && !AcceptsNull(destinationType))
                Report(Diagnostics.NullableToNonNullable, sourceName, Short(owner) + "." + destinationName);
            if (fromParameter == ParameterMatch.Found) config.UsedParameters.Add(path[0].Name);
            var notes = CommitTrace(trace, sourceName, owner, destinationName, config, projection: false);
            if (fromParameter == ParameterMatch.Found) notes.Insert(0, "method parameter");
            RecordFlow(source, owner, destinationName, destinationMember, path, notes);
            return value;
        }

        // ================================================================== [MapConstant] [MapFrom] [MapRequired] [MapTruncate]

        /// <summary>[MapConstant] or [MapFrom]: the member's value when one of them applies (value is null after an error).</summary>
        private bool TryAttributeValue(ITypeSymbol source, string src, string name, ITypeSymbol destinationType, ISymbol destinationSymbol,
            ITypeSymbol owner, MemberConfig config, bool projection, out string? value, string? rowName = null)
        {
            rowName ??= name;
            value = null;
            if (config.Constants.TryGetValue(name, out var constant))
            {
                if (constant.IsNull)
                {
                    if (!AcceptsNull(destinationType))
                        Report(Diagnostics.InvalidValueAttribute, "MapConstant", name, _current?.Name ?? string.Empty, Short(destinationType) + " cannot be null");
                    else value = "default(" + T(destinationType) + ")";
                }
                else
                {
                    var literal = Literal(constant);
                    if (literal == null || constant.Type == null || !_compilation.ClassifyConversion(constant.Type, destinationType).IsImplicit)
                        Report(Diagnostics.InvalidValueAttribute, "MapConstant", name, _current?.Name ?? string.Empty,
                            "the value must be a constant that converts implicitly to " + Short(destinationType));
                    else value = literal;
                }
                if (value != null)
                    RecordComputed(source, owner, rowName, destinationSymbol, "constant `" + (constant.IsNull ? "null" : constant.ToCSharpString()) + "`", new List<string> { "constant" });
                return true;
            }

            if (!config.Computed.TryGetValue(name, out var methodName)) return false;
            var method = FindComputeMethod(methodName, source, config);
            if (method == null)
            {
                Report(Diagnostics.InvalidValueAttribute, "MapFrom", name, _current?.Name ?? string.Empty,
                    "the mapper class needs a static method '" + methodName + "' that takes " + Short(source) +
                    (config.Parameters.Count > 0 ? " (then, optionally, extra parameters of the mapping method by name)" : string.Empty));
                return true;
            }
            var arguments = new List<string> { src };
            foreach (var p in method.Parameters.Skip(1))
            {
                var parameter = config.Parameters[p.Name];
                arguments.Add(Escape(parameter.Name));
                config.UsedParameters.Add(parameter.Name);
            }

            string call;
            var notes = new List<string> { "computed" };
            if (projection)
            {
                var inlined = InlineExpressionBody(method, arguments);
                if (inlined != null)
                {
                    call = "(" + inlined + ")";
                    notes.Add("inlined");
                }
                else
                {
                    call = Escape(method.Name) + "(" + string.Join(", ", arguments) + ")";
                    if (!ProjectionProvider.RunsInMemory(_provider))
                        Report(Diagnostics.ProjectionClientEvaluation, _current?.Name ?? string.Empty, Short(owner) + "." + name, "the method '" + method.Name + "'",
                            "is not a single expression, so EF Core runs it in .NET after the query (only allowed in the final Select)", name);
                }
            }
            else
            {
                call = Escape(method.Name) + "(" + string.Join(", ", arguments) + ")";
            }

            var returned = method.ReturnType;
            value = projection
                ? Traced(() => ProjectionValue(returned, destinationType, call, 0, new HashSet<string>(StringComparer.Ordinal)), out _)
                : Traced(() => Convert(returned, destinationType, call, 0), out _);
            if (value == null)
            {
                Report(Diagnostics.InvalidValueAttribute, "MapFrom", name, _current?.Name ?? string.Empty,
                    "'" + method.Name + "' returns " + Short(returned) + ", which cannot be converted to " + Short(destinationType));
                return true;
            }
            RecordComputed(source, owner, rowName, destinationSymbol, "`" + method.Name + "(...)`", notes);
            return true;
        }

        /// <summary>A static method of the mapper that takes the source first, then extra parameters of the mapping method by name.</summary>
        private IMethodSymbol? FindComputeMethod(string name, ITypeSymbol source, MemberConfig config)
        {
            foreach (var candidate in _mapper.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (!candidate.IsStatic || candidate.MethodKind != MethodKind.Ordinary || candidate.IsGenericMethod || candidate.ReturnsVoid ||
                    candidate.Parameters.Length == 0 || IsPartialDefinition(candidate)) continue;
                if (!_compilation.ClassifyConversion(source, candidate.Parameters[0].Type).IsImplicit) continue;
                var ok = true;
                foreach (var p in candidate.Parameters.Skip(1))
                    if (!config.Parameters.TryGetValue(p.Name, out var parameter) || !_compilation.ClassifyConversion(parameter.Type, p.Type).IsImplicit)
                    {
                        ok = false;
                        break;
                    }
                if (ok) return candidate;
            }
            return null;
        }

        /// <summary>Namespaces the inlined method bodies need (extension methods), added to the generated file.</summary>
        private readonly SortedSet<string> _extraUsings = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The body of an expression-bodied (or single-return) method with its parameters replaced by the arguments and its
        /// type names fully qualified, so it can sit inside a projection's expression tree; null when it can't be inlined.
        /// </summary>
        private string? InlineExpressionBody(IMethodSymbol method, List<string> arguments)
        {
            if (!(method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_ct) is MethodDeclarationSyntax syntax)) return null;
            var body = syntax.ExpressionBody?.Expression ??
                       (syntax.Body != null && syntax.Body.Statements.Count == 1 && syntax.Body.Statements[0] is ReturnStatementSyntax ret ? ret.Expression : null);
            if (body == null) return null;
            // Not allowed in expression trees.
            if (body.DescendantNodesAndSelf().Any(n => n is ConditionalAccessExpressionSyntax || n is ThrowExpressionSyntax || n is IsPatternExpressionSyntax ||
                                                       n is SwitchExpressionSyntax || n is AssignmentExpressionSyntax || n is DeclarationExpressionSyntax ||
                                                       n is TupleExpressionSyntax || n is RangeExpressionSyntax || n is RefExpressionSyntax ||
                                                       n is StackAllocArrayCreationExpressionSyntax || n.IsKind(SyntaxKind.IndexExpression)))
                return null;

            var model = _compilation.GetSemanticModel(syntax.SyntaxTree);
            var replacements = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            for (var i = 0; i < method.Parameters.Length; i++) replacements[method.Parameters[i]] = arguments[i];
            var rewritten = new InlineRewriter(model, replacements).Visit(body);
            foreach (var directive in syntax.SyntaxTree.GetCompilationUnitRoot(_ct).Usings)
                if (directive.Alias == null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Name != null) _extraUsings.Add(directive.Name.ToString());
            return rewritten.ToString();
        }

        private sealed class InlineRewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _model;
            private readonly Dictionary<ISymbol, string> _replacements;

            public InlineRewriter(SemanticModel model, Dictionary<ISymbol, string> replacements)
            {
                _model = model;
                _replacements = replacements;
            }

            public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            {
                var symbol = _model.GetSymbolInfo(node).Symbol;
                if (symbol is IParameterSymbol && _replacements.TryGetValue(symbol, out var argument))
                    return SyntaxFactory.ParseExpression("(" + argument + ")").WithTriviaFrom(node);
                if (symbol is INamedTypeSymbol type && IsLeftmost(node))
                    return SyntaxFactory.ParseExpression(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).WithTriviaFrom(node);
                if (symbol != null && symbol.IsStatic && !(symbol is ITypeSymbol) && symbol.ContainingType != null && IsLeftmost(node) &&
                    !(node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node && symbol is IMethodSymbol m && m.IsExtensionMethod))
                    return SyntaxFactory.ParseExpression(symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + node.Identifier.ValueText).WithTriviaFrom(node);
                return base.VisitIdentifierName(node);
            }

            public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
            {
                if (_model.GetSymbolInfo(node).Symbol is INamedTypeSymbol type && IsLeftmost(node))
                    return SyntaxFactory.ParseExpression(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).WithTriviaFrom(node);
                return base.VisitGenericName(node);
            }

            /// <summary>Not the right-hand name of a member access or qualified name.</summary>
            private static bool IsLeftmost(SimpleNameSyntax node) =>
                !(node.Parent is MemberAccessExpressionSyntax access && access.Name == node) &&
                !(node.Parent is QualifiedNameSyntax qualified && qualified.Right == node) &&
                !(node.Parent is AliasQualifiedNameSyntax);
        }

        /// <summary>[MapRequired]: "(value ?? throw new InvalidOperationException(...))", so a missing value is an error, not a default.</summary>
        private MemberAccess ApplyRequired(MemberAccess access, string name, ITypeSymbol owner, string sourceName, MemberConfig config, bool projection)
        {
            if (!config.Required.Contains(name)) return access;
            if (projection)
            {
                Report(Diagnostics.InvalidValueAttribute, "MapRequired", name, _current?.Name ?? string.Empty, "a projection cannot throw; filter the query instead");
                return access;
            }
            if (config.NullSubstitutes.ContainsKey(name))
            {
                Report(Diagnostics.InvalidValueAttribute, "MapRequired", name, _current?.Name ?? string.Empty, "it also has [MapNullSubstitute]; use one or the other");
                return access;
            }
            ITypeSymbol valueType;
            if (MemberResolver.IsNullableValueType(access.Type, out var underlying)) valueType = underlying;
            else if (access.Type.IsReferenceType) valueType = access.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            else
            {
                Report(Diagnostics.InvalidValueAttribute, "MapRequired", name, _current?.Name ?? string.Empty, "its source (" + Short(access.Type) + ") can never be null");
                return access;
            }
            var message = SymbolDisplay.FormatLiteral((_current?.Name ?? "Map") + ": " + Short(owner) + "." + name + " is required, but " + sourceName + " is null.", true);
            return new MemberAccess("(" + access.Expression + " ?? throw new global::System.InvalidOperationException(" + message + "))", valueType, false);
        }

        private readonly HashSet<string> _reportedLengths = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>[MapTruncate] cuts strings to the destination's maximum length; without it, CM0027 when the source can be longer.</summary>
        private string ApplyLength(string value, List<ISymbol>? path, string name, ITypeSymbol destinationType, ITypeSymbol owner, MemberConfig config, bool projection, string sourceName)
        {
            if (destinationType.SpecialType != SpecialType.System_String) return value;
            var memberName = DestinationMemberName(owner, name);
            var maximum = MaxLength(owner, memberName);
            if (config.Truncate.TryGetValue(name, out var length))
            {
                if (length <= 0) length = maximum ?? 0;
                if (length <= 0)
                {
                    Report(Diagnostics.InvalidValueAttribute, "MapTruncate", name, _current?.Name ?? string.Empty,
                        "no maximum length: give one ([MapTruncate(\"" + name + "\", 50)]) or mark the member [MaxLength]");
                    return value;
                }
                var n = length.ToString(CultureInfo.InvariantCulture);
                if (projection) return "(" + value + ".Length > " + n + " ? " + value + ".Substring(0, " + n + ") : " + value + ")";
                return TruncateHelper() + "(" + value + ", " + n + ")";
            }

            if (maximum == null || path == null || path.Count == 0 || path[0] is IParameterSymbol) return value;
            if (_currentReviewed.Contains(memberName) || _classReviewed.Contains(memberName)) return value;
            var last = path[path.Count - 1];
            if (MemberResolver.TypeOf(last).SpecialType != SpecialType.System_String) return value;
            var sourceMaximum = last.ContainingType != null ? MaxLength(last.ContainingType, last.Name) : null;
            if (sourceMaximum != null && sourceMaximum <= maximum) return value;
            if (_reportedLengths.Add(_current?.Name + "|" + T(owner) + "|" + memberName))
                Report(IsEntity(owner) ? Diagnostics.PossibleTruncation : Diagnostics.PossibleTruncationInfo,
                    sourceName, maximum.Value.ToString(CultureInfo.InvariantCulture), Short(owner) + "." + memberName, memberName);
            return value;
        }

        private string TruncateHelper()
        {
            const string key = "helper:truncate";
            if (_helperNames.TryGetValue(key, out var existing)) return existing;
            var name = "__Truncate";
            _helperNames[key] = name;
            var w = new CodeWriter(_memberIndent);
            w.Line();
            w.Line("private static string? " + name + "(string? value, int length) =>");
            w.Indent();
            w.Line("value is null || value.Length <= length ? value : value.Substring(0, length);");
            w.Outdent();
            _helpers.Append(w.ToString());
            return name;
        }

        /// <summary>
        /// Convention binding for a destination name. More than one possible source (a property and a Get-method, two members
        /// differing only in case, or two flattenings) is error CM0015 rather than a silent pick; returns null then.
        /// </summary>
        private List<ISymbol>? FindConventionPath(ITypeSymbol source, string name, ITypeSymbol owner, out bool ambiguous)
        {
            ambiguous = false;
            var candidates = _resolver.FindCandidatePaths(source, name);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            ambiguous = true;
            ReportAmbiguousSource(source, name, owner, candidates.Select(c => "'" + Short(source) + "." + PathText(c) + "'"));
            return null;
        }

        private void ReportAmbiguousSource(ITypeSymbol source, string name, ITypeSymbol owner, IEnumerable<string> candidates)
        {
            if (_reportedAmbiguities.Add("source|" + _current?.Name + "|" + T(source) + "|" + T(owner) + "|" + name.ToUpperInvariant()))
                Report(Diagnostics.AmbiguousSource, name, Short(owner), string.Join(" or ", candidates));
        }

        private static string PathText(IEnumerable<ISymbol> path) => string.Join(".", path.Select(m => m is IMethodSymbol ? m.Name + "()" : m.Name));

        /// <summary>
        /// Reports what the member's conversion did (CM0016 for parsing and narrowing, CM0021 for projection parts that may not
        /// translate), records the conversion methods it used, and returns the notes for the data-flow report.
        /// </summary>
        private List<string> CommitTrace(Trace trace, string sourceName, ITypeSymbol owner, string destinationName, MemberConfig config, bool projection)
        {
            var notes = new List<string>();
            var target = Short(owner) + "." + destinationName;
            foreach (var method in trace.Methods.Distinct<IMethodSymbol>(SymbolEqualityComparer.Default))
            {
                if (!_conversionUses.TryGetValue(method, out var uses))
                {
                    uses = new SortedSet<string>(StringComparer.Ordinal);
                    _conversionUses[method] = uses;
                }
                uses.Add(target);
                notes.Add("via `" + method.Name + "()`");
                if (projection && !ProjectionProvider.RunsInMemory(_provider))
                    Report(Diagnostics.ProjectionClientEvaluation, _current?.Name ?? string.Empty, target,
                        "the method '" + method.Name + "'", "EF Core runs in .NET after the query (only allowed in the final Select, never in a filter or sort)", destinationName);
            }

            var risks = trace.Risks.Distinct(StringComparer.Ordinal).ToList();
            if (risks.Count > 0)
            {
                if (config.AllowParse.Contains(destinationName) || _classAllowParse.Contains(destinationName))
                    notes.Add("parsed (allowed)");
                else
                {
                    Report(Diagnostics.LossyConversion, sourceName, target, string.Join(" and ", risks), destinationName);
                    notes.Add("⚠ " + string.Join(", ", risks));
                    _riskyFlows.Add(target + ": " + string.Join(", ", risks));
                }
            }

            foreach (var problem in trace.ClientRisks.Distinct(StringComparer.Ordinal))
            {
                var split = problem.IndexOf('|');
                Report(Diagnostics.ProjectionClientEvaluation, _current?.Name ?? string.Empty, target, problem.Substring(0, split), problem.Substring(split + 1), destinationName);
                notes.Add("⚠ may not translate");
            }
            return notes;
        }

        private enum ParameterMatch { None, Found, Invalid }

        /// <summary>
        /// An extra method parameter as the source of a destination member: a parameter with the member's name, or a
        /// [MapMember] path that starts with a parameter name ("policy" or "policy.Number").
        /// </summary>
        private ParameterMatch ResolveParameter(ITypeSymbol source, string destinationName, ITypeSymbol owner, MemberConfig config, out List<ISymbol> path, out MemberAccess access)
        {
            path = null!;
            access = default;
            if (config.Parameters.Count == 0) return ParameterMatch.None;

            IParameterSymbol? parameter;
            var rest = string.Empty;
            if (config.SourcePrefixed.Contains(destinationName)) return ParameterMatch.None;
            if (config.Explicit.TryGetValue(destinationName, out var explicitPath))
            {
                var dot = explicitPath.IndexOf('.');
                var head = dot < 0 ? explicitPath : explicitPath.Substring(0, dot);
                if (!config.Parameters.TryGetValue(head, out parameter)) return ParameterMatch.None;
                rest = dot < 0 ? string.Empty : explicitPath.Substring(dot + 1);
                // Names match case-insensitively, so "CreateUser" also matches a parameter "createUser". The exact case
                // decides ("CreateUser" is the source member, "createUser" the parameter); otherwise it is CM0014, never a guess.
                var sourceMatch = _resolver.FindExplicitPath(source, explicitPath);
                var exactParameter = string.Equals(head, parameter.Name, StringComparison.Ordinal);
                var exactSource = sourceMatch != null && string.Equals(head, sourceMatch[0].Name, StringComparison.Ordinal);
                if (exactSource && !exactParameter) return ParameterMatch.None;
                if (sourceMatch != null && exactParameter == exactSource)
                {
                    if (_reportedAmbiguities.Add(_current?.Name + "|" + destinationName))
                        Report(Diagnostics.AmbiguousMapMemberSource, explicitPath, destinationName,
                            Short(source) + "." + string.Join(".", sourceMatch.Select(m => m.Name)), parameter.Name,
                            (_current != null && _current.Parameters.Length > 0 ? _current.Parameters[0].Name : "source") + "." + explicitPath);
                    return ParameterMatch.Invalid;
                }
            }
            else if (!config.Parameters.TryGetValue(destinationName, out parameter))
            {
                return ParameterMatch.None;
            }
            else
            {
                // A parameter wins over a source member with the destination's own name (the caller supplies it on purpose),
                // but not over a flattening such as "CustomerName" -> Customer.Name: that is a guess either way.
                var candidates = _resolver.FindCandidatePaths(source, destinationName);
                if (candidates.Count > 0 && candidates.All(c => c.Count > 1))
                {
                    ReportAmbiguousSource(source, destinationName, owner,
                        new[] { "the parameter '" + parameter.Name + "'" }.Concat(candidates.Select(c => "'" + Short(source) + "." + PathText(c) + "'")));
                    return ParameterMatch.Invalid;
                }
            }

            var members = new List<ISymbol>();
            if (rest.Length > 0)
            {
                var found = _resolver.FindExplicitPath(parameter.Type, rest);
                if (found == null)
                {
                    if (_reportedAmbiguities.Add(_current?.Name + "|" + destinationName))
                        Report(Diagnostics.UnknownAttributeMember, explicitPath!, "MapMember", parameter.Name);
                    return ParameterMatch.Invalid;
                }
                members = found;
            }
            access = BuildPath(Escape(parameter.Name), parameter.Type, members, rootMayBeNull: true);
            path = new List<ISymbol> { parameter };
            path.AddRange(members);
            return ParameterMatch.Found;
        }

        private static string Describe(ITypeSymbol source, List<ISymbol> path, ParameterMatch fromParameter) =>
            fromParameter == ParameterMatch.Found
                ? "parameter " + string.Join(".", path.Select(m => m.Name))
                : Short(source) + "." + string.Join(".", path.Select(m => m.Name));

        /// <summary>[MapNullSubstitute]: "(value ?? constant)", typed as the non-nullable value.</summary>
        private MemberAccess ApplyNullSubstitute(MemberAccess access, string destinationName, ITypeSymbol destinationType, MemberConfig config)
        {
            if (!config.NullSubstitutes.TryGetValue(destinationName, out var constant)) return access;

            ITypeSymbol valueType;
            if (MemberResolver.IsNullableValueType(access.Type, out var underlying)) valueType = underlying;
            else if (access.Type.IsReferenceType) valueType = access.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            else
            {
                Report(Diagnostics.NullSubstituteNeverNull, destinationName, Short(access.Type));
                return access;
            }

            var literal = Literal(constant);
            if (literal == null || constant.Type == null || !_compilation.ClassifyConversion(constant.Type, valueType).IsImplicit)
            {
                Report(Diagnostics.InvalidNullSubstitute, destinationName, Short(valueType));
                return access;
            }
            return new MemberAccess("(" + access.Expression + " ?? " + literal + ")", valueType, false);
        }

        /// <summary>C# source for an attribute constant, or null for arrays, types and null.</summary>
        private static string? Literal(TypedConstant constant)
        {
            if (constant.IsNull || constant.Type == null) return null;
            if (constant.Kind == TypedConstantKind.Enum)
                return "((" + T(constant.Type) + ")(" + SymbolDisplay.FormatPrimitive(constant.Value!, false, false) + "))";
            if (constant.Kind != TypedConstantKind.Primitive) return null;
            switch (constant.Value)
            {
                case string s: return SymbolDisplay.FormatLiteral(s, true);
                case char c: return SymbolDisplay.FormatLiteral(c, true);
                case bool b: return b ? "true" : "false";
                case double d when double.IsNaN(d) || double.IsInfinity(d): return null;
                case float f when float.IsNaN(f) || float.IsInfinity(f): return null;
                default: return "((" + T(constant.Type) + ")(" + SymbolDisplay.FormatPrimitive(constant.Value!, false, false) + "))";
            }
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
        private MemberAccess BuildPath(string src, ITypeSymbol sourceType, List<ISymbol> path, bool rootMayBeNull = false)
        {
            var sb = new StringBuilder(src);
            var current = sourceType;
            var lifted = false;
            var mayBeNull = false;
            for (var i = 0; i < path.Count; i++)
            {
                var member = path[i];
                if ((i > 0 || rootMayBeNull) && current.IsReferenceType)
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
            {
                _trace?.Methods.Add(userMethod);
                return Escape(userMethod.Name) + "(" + expr + ")";
            }

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
            {
                AddRisk("Enum.Parse");
                return "(" + T(d) + ")global::System.Enum.Parse(typeof(" + T(d) + "), " + expr + ", true)";
            }
            if ((s.TypeKind == TypeKind.Enum && IsIntegral(d)) || (IsIntegral(s) && d.TypeKind == TypeKind.Enum))
                return "(" + T(d) + ")(" + expr + ")";

            if (s.SpecialType == SpecialType.System_String)
            {
                var parse = ParseCall(d, expr);
                if (parse != null)
                {
                    AddRisk(Short(d) + ".Parse");
                    return parse;
                }
            }

            if (conversion.Exists && conversion.IsExplicit && conversion.IsNumeric)
            {
                AddRisk("a narrowing cast (" + Short(s) + " to " + Short(d) + ")");
                return "checked((" + T(d) + ")(" + expr + "))";
            }

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

        private void AddRisk(string conversion) => _trace?.Risks.Add(conversion);

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
            if (_helperNames.TryGetValue(key, out var existing))
            {
                if (_helperTraces.TryGetValue(existing, out var cached)) _trace?.MergeFrom(cached);
                return existing;
            }
            var outer = _trace;
            var name = Traced(() => BuildCollectionHelper(source, destination, sourceElement, kind, depth, key), out var helperTrace);
            _trace = outer;
            if (name != null)
            {
                _helperTraces[name] = helperTrace;
                outer?.MergeFrom(helperTrace);
            }
            return name;
        }

        private string? BuildCollectionHelper(ITypeSymbol source, ITypeSymbol destination, ITypeSymbol sourceElement, CollectionKind kind, int depth, string key)
        {

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

            // Extra parameters are query parameters (projections with parameters).
            if (definition == "System.Linq.IQueryable<T>" && method.Parameters.Length >= 1 &&
                method.Parameters[0].Type is INamedTypeSymbol parameter && parameter.IsGenericType &&
                parameter.OriginalDefinition.ToDisplayString() == "System.Linq.IQueryable<T>")
            {
                source = parameter.TypeArguments[0];
                destination = returnType.TypeArguments[0];
                isQueryable = true;
                return true;
            }

            if (definition == "System.Linq.Expressions.Expression<TDelegate>" &&
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
            // Projections with parameters: the values are captured, so the provider sends them as query parameters.
            foreach (var extra in method.Parameters.Skip(isQueryable ? 1 : 0)) config.Parameters[extra.Name] = extra;
            var x = config.Parameters.ContainsKey("x") ? "__x" : "x";

            var stack = new HashSet<string>(StringComparer.Ordinal) { PairKey(source, destination) };
            var body = ProjectionObject(source, (INamedTypeSymbol)destination, x, config, 0, stack);
            CheckConfigUsage(config, method, destination);
            foreach (var extra in config.Parameters.Values)
                if (!config.UsedParameters.Contains(extra.Name)) Report(Diagnostics.UnusedParameter, extra.Name, method.Name);
            if (body == null)
            {
                w.Line("throw new global::System.NotSupportedException(\"" + Short(destination) + " has no usable constructor.\");");
                return;
            }

            var expressionType = "global::System.Linq.Expressions.Expression<global::System.Func<" + T(source) + ", " + T(destination) + ">>";
            string expression;
            if (config.Parameters.Count == 0)
            {
                // No parameters: one cached expression, which also lets EF Core cache the query.
                var field = "__projection" + (++_counter).ToString(CultureInfo.InvariantCulture) + "_" + Identifier(source) + "_" + Identifier(destination);
                var helper = new CodeWriter(_memberIndent);
                helper.Line();
                helper.Line("private static readonly " + expressionType + " " + field + " =");
                helper.Indent();
                helper.Line(x + " => " + body + ";");
                helper.Outdent();
                _helpers.Append(helper.ToString());
                expression = field;
            }
            else
            {
                w.Line(expressionType + " __projection = " + x + " => " + body + ";");
                expression = "__projection";
            }

            w.Line(isQueryable
                ? "return global::System.Linq.Queryable.Select(" + Escape(method.Parameters[0].Name) + ", " + expression + ");"
                : "return " + expression + ";");
        }

        /// <summary>A projection's extra parameter as the source of a member: by the member's name, or a [MapMember] path starting with it.</summary>
        private string? ProjectionParameter(string name, ITypeSymbol destinationType, ISymbol destinationSymbol, ITypeSymbol source, ITypeSymbol owner,
            MemberConfig config, int depth, HashSet<string> stack, string? rowName = null)
        {
            if (config.Parameters.Count == 0 || config.SourcePrefixed.Contains(name)) return null;
            IParameterSymbol? parameter;
            var rest = string.Empty;
            if (config.Explicit.TryGetValue(name, out var explicitPath))
            {
                var dot = explicitPath.IndexOf('.');
                var head = dot < 0 ? explicitPath : explicitPath.Substring(0, dot);
                if (!config.Parameters.TryGetValue(head, out parameter) || !string.Equals(parameter.Name, head, StringComparison.Ordinal)) return null;
                rest = dot < 0 ? string.Empty : explicitPath.Substring(dot + 1);
            }
            else if (!config.Parameters.TryGetValue(name, out parameter))
            {
                return null;
            }

            var members = rest.Length == 0 ? new List<ISymbol>() : _resolver.FindExplicitPath(parameter.Type, rest);
            if (members == null)
            {
                Report(Diagnostics.UnknownAttributeMember, explicitPath!, "MapMember", parameter.Name);
                return null;
            }
            var path = new List<ISymbol> { parameter };
            path.AddRange(members);
            var valueType = members.Count == 0 ? parameter.Type : MemberResolver.TypeOf(members[members.Count - 1]);
            var expression = ProjectionPath(Escape(parameter.Name), members);
            var value = Traced(() => ProjectionValue(valueType, destinationType, expression, depth, stack), out _);
            if (value == null)
            {
                Report(Diagnostics.IncompatibleTypes, "parameter " + PathText(path), Short(valueType), Short(owner) + "." + name, Short(destinationType), name);
                return null;
            }
            config.UsedParameters.Add(parameter.Name);
            RecordFlow(source, owner, rowName ?? name, destinationSymbol, path, new List<string> { "projection", "query parameter" });
            return value;
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
                var paths = new List<List<ISymbol>?>();
                var traces = new List<Trace?>();
                var ok = true;
                foreach (var p in ctor.Parameters)
                {
                    // [MapConstant], [MapFrom] and query parameters (already recorded in the report).
                    string? special = null;
                    var handled = false;
                    if (!config.Ignored.Contains(p.Name) && config.Includes(p.Name))
                    {
                        if (config.Constants.ContainsKey(p.Name) || config.Computed.ContainsKey(p.Name))
                        {
                            handled = true;
                            TryAttributeValue(source, src, p.Name, p.Type, p, destination, config, true, out special, p.Name + " (constructor)");
                        }
                        else if ((special = ProjectionParameter(p.Name, p.Type, p, source, destination, config, depth, stack, p.Name + " (constructor)")) != null)
                        {
                            handled = true;
                        }
                    }
                    if (handled)
                    {
                        if (special == null)
                        {
                            ok = false;
                            break;
                        }
                        args.Add(special);
                        paths.Add(null);
                        traces.Add(null);
                        continue;
                    }
                    var path = config.Ignored.Contains(p.Name) || !config.Includes(p.Name) ? null
                        : config.Explicit.TryGetValue(p.Name, out var explicitPath) ? _resolver.FindExplicitPath(source, explicitPath)
                        : FindConventionPath(source, p.Name, destination, out _);
                    if (path == null || (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(p) &&
                                         !destination.GetMembers().Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase) && MemberResolver.IsSensitive(m))))
                    {
                        ok = false;
                        break;
                    }
                    var projected = ProjectionSubstitute(ProjectionPath(src, path), MemberResolver.TypeOf(path[path.Count - 1]), p.Name, config, out var projectedType);
                    var value = Traced(() => ProjectionValue(projectedType, p.Type, projected, depth, stack), out var trace);
                    if (value == null)
                    {
                        ok = false;
                        break;
                    }
                    args.Add(value);
                    paths.Add(path);
                    traces.Add(trace);
                }
                if (!ok) continue;
                chosen = ctor;
                arguments = args;
                for (var i = 0; i < ctor.Parameters.Length; i++)
                {
                    var p = ctor.Parameters[i];
                    if (paths[i] == null) continue;
                    var notes = CommitTrace(traces[i]!, Short(source) + "." + PathText(paths[i]!), destination, p.Name, config, projection: true);
                    notes.Insert(0, "projection, constructor parameter");
                    RecordFlow(source, destination, p.Name + " (constructor)", p, paths[i]!, notes);
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
                if (!config.Includes(member.Name))
                {
                    RecordSkipped(source, destination, member);
                    continue;
                }
                var value = ProjectionMember(source, src, member, destination, config, depth, stack);
                if (value != null) assignments.Add(Escape(member.Name) + " = " + value);
            }

            var creation = "new " + T(destination) + "(" + string.Join(", ", arguments) + ")";
            return assignments.Count == 0 ? creation : creation + " { " + string.Join(", ", assignments) + " }";
        }

        private string? ProjectionMember(ITypeSymbol source, string src, ISymbol destinationMember, ITypeSymbol owner, MemberConfig config, int depth, HashSet<string> stack)
        {
            var name = destinationMember.Name;
            var memberType = MemberResolver.TypeOf(destinationMember);
            if (TryAttributeValue(source, src, name, memberType, destinationMember, owner, config, projection: true, out var attributeValue))
                return attributeValue == null ? null : ApplyLength(attributeValue, null, name, memberType, owner, config, true, name);
            var fromParameter = ProjectionParameter(name, memberType, destinationMember, source, owner, config, depth, stack);
            if (fromParameter != null) return fromParameter;
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
                path = FindConventionPath(source, name, owner, out var ambiguous);
                if (path == null)
                {
                    if (!ambiguous) ReportUnmapped(name, owner, source, config);
                    return null;
                }
            }

            var sourceName = Short(source) + "." + string.Join(".", path.Select(m => m.Name));
            if (path.Any(MemberResolver.IsSensitive) && !MemberResolver.IsSensitive(destinationMember))
            {
                Report(Diagnostics.SensitiveLeak, sourceName, Short(owner) + "." + name, name);
                return null;
            }

            var destinationType = MemberResolver.TypeOf(destinationMember);
            if (config.Required.Contains(name)) ApplyRequired(new MemberAccess(src, source, true), name, owner, sourceName, config, projection: true);
            var expression = ProjectionSubstitute(ProjectionPath(src, path), MemberResolver.TypeOf(path[path.Count - 1]), name, config, out var valueType);
            var value = Traced(() => ProjectionValue(valueType, destinationType, expression, depth, stack), out var trace);
            if (value == null)
            {
                Report(Diagnostics.IncompatibleTypes, sourceName, Short(valueType), Short(owner) + "." + name, Short(destinationType), name);
                return null;
            }
            value = ApplyLength(value, path, name, destinationType, owner, config, true, sourceName);
            var notes = CommitTrace(trace, sourceName, owner, name, config, projection: true);
            notes.Insert(0, "projection");
            RecordFlow(source, owner, name, destinationMember, path, notes);
            return value;
        }

        /// <summary>[MapNullSubstitute] in a projection: "x.Status ?? \"\"" (COALESCE in SQL).</summary>
        private string ProjectionSubstitute(string expression, ITypeSymbol type, string destinationName, MemberConfig config, out ITypeSymbol valueType)
        {
            var access = ApplyNullSubstitute(new MemberAccess(expression, type, true), destinationName, type, config);
            valueType = access.Type;
            return access.Expression;
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
            if (_conversionMethods.TryGetValue(PairKey(s, d), out var method))
            {
                _trace?.Methods.Add(method);
                return Escape(method.Name) + "(" + expr + ")";
            }

            var conversion = _compilation.ClassifyConversion(s, d);
            if (conversion.Exists && conversion.IsImplicit) return expr;

            var sIsNullable = MemberResolver.IsNullableValueType(s, out var sUnderlying);
            var dIsNullable = MemberResolver.IsNullableValueType(d, out var dUnderlying);

            if (d.SpecialType == SpecialType.System_String)
            {
                if (!IsScalar(sUnderlying)) return null;
                var problem = ProjectionProvider.ToStringProblem(_provider, sUnderlying);
                if (problem != null) _trace?.ClientRisks.Add("ToString() on " + Short(sUnderlying) + "|" + problem);
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
            if (conversion.Exists && conversion.IsExplicit && conversion.IsNumeric)
            {
                AddRisk("a narrowing cast (" + Short(s) + " to " + Short(d) + ")");
                return "(" + T(d) + ")(" + expr + ")";
            }

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

        /// <summary>One table of the report: a mapping method (overloads are grouped by name) or a nested type pair.</summary>
        private sealed class FlowSection
        {
            public FlowSection(string heading, IMethodSymbol? method)
            {
                Heading = heading;
                Method = method;
            }

            public string Heading { get; }

            /// <summary>The mapping method this section belongs to; null for nested maps.</summary>
            public IMethodSymbol? Method { get; }
            public SortedDictionary<string, string> Rows { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Destination member -> (source, notes), for the side-by-side comparison of sibling maps.</summary>
            public SortedDictionary<string, KeyValuePair<string, string>> Cells { get; } = new SortedDictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);

            /// <summary>The destination type (as shown).</summary>
            public string Destination { get; set; } = string.Empty;
        }

        private static readonly SymbolDisplayFormat MethodDisplayFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <summary>"ToImportData(LocationData, string?, decimal?)": how a method is named in the report and in WriteSets.</summary>
        private static string MethodDisplay(IMethodSymbol method) => method.ToDisplayString(MethodDisplayFormat);

        private FlowSection RecordMap(ITypeSymbol source, ITypeSymbol destination)
        {
            string key;
            FlowSection? section;
            var isRoot = _current != null && _rootSource != null && _rootDestination != null &&
                         SymbolEqualityComparer.Default.Equals(source, _rootSource) && SymbolEqualityComparer.Default.Equals(destination, _rootDestination);
            if (isRoot)
            {
                key = "1|" + _current!.Name + "|" + _methodOrder[_current].ToString("D4", CultureInfo.InvariantCulture);
                if (!_flows.TryGetValue(key, out section))
                {
                    section = new FlowSection("`" + MethodDisplay(_current) + "` → `" + Short(destination) + "`", _current) { Destination = Short(destination) };
                    _flows[key] = section;
                }
            }
            else
            {
                key = "2|`" + Short(source) + "` → `" + Short(destination) + "`";
                if (!_flows.TryGetValue(key, out section))
                {
                    section = new FlowSection(key.Substring(2), null);
                    _flows[key] = section;
                }
            }
            foreach (var member in _resolver.GetReadableMembers(source))
                if (MemberResolver.IsSensitive(member)) SensitiveTargets(member);
            return section;
        }

        private void RecordIgnored(ITypeSymbol source, ITypeSymbol destination, ISymbol member)
        {
            var section = RecordMap(source, destination);
            section.Rows[member.Name] = "| " + DestinationCell(member.Name, member) + " | — | ignored |";
            section.Cells[member.Name] = new KeyValuePair<string, string>("—", "ignored");
        }

        private void RecordSkipped(ITypeSymbol source, ITypeSymbol destination, ISymbol member)
        {
            var section = RecordMap(source, destination);
            section.Rows[member.Name] = "| " + DestinationCell(member.Name, member) + " | — | not listed ([MapExplicit]) |";
            section.Cells[member.Name] = new KeyValuePair<string, string>("—", "not listed");
        }

        private void RecordFlow(ITypeSymbol source, ITypeSymbol destination, string destinationName, ISymbol destinationSymbol, List<ISymbol> path, IEnumerable<string>? extraNotes)
        {
            var notes = new List<string>();
            if (extraNotes != null) notes.AddRange(extraNotes);
            if (path.Count > 1 && !(path[0] is IParameterSymbol)) notes.Add("flattened");
            var memberName = DestinationMemberName(destination, destinationName);
            foreach (var member in path.Where(MemberResolver.IsSensitive))
            {
                notes.Add("sensitive");
                SensitiveTargets(member).Add(Short(destination) + "." + memberName);
            }
            foreach (var member in path) SuggestSensitive(member);
            SuggestSensitive(destinationSymbol);
            if (!(path[0] is IParameterSymbol))
            {
                foreach (var member in path) RecordRead(member);
                if (notes.All(n => !n.StartsWith("via ", StringComparison.Ordinal)))
                    RecordLineage(source, destination, memberName, PathText(path));
            }
            RecordRow(source, destination, destinationName, destinationSymbol, "`" + PathText(path) + "`", notes,
                path[0] is IParameterSymbol ? "parameter " + PathText(path) : PathText(path));
        }

        /// <summary>A value that does not come from a source member: a constant or a [MapFrom] method.</summary>
        private void RecordComputed(ITypeSymbol source, ITypeSymbol destination, string destinationName, ISymbol destinationSymbol, string description, List<string> notes) =>
            RecordRow(source, destination, destinationName, destinationSymbol, description, notes, description.Trim('`'));

        private void RecordRow(ITypeSymbol source, ITypeSymbol destination, string destinationName, ISymbol destinationSymbol, string description, List<string> notes, string sourcePath)
        {
            var memberName = DestinationMemberName(destination, destinationName);
            var reviewed = _currentReviewed.Contains(memberName) || _classReviewed.Contains(memberName);
            if (IsKey(destination, memberName))
            {
                if (reviewed)
                    notes.Add("key (reviewed)");
                else
                {
                    notes.Add("⚠ key");
                    _riskyFlows.Add("`" + Short(destination) + "." + memberName + "` (key) is written from " + description);
                }
            }
            CheckDatabaseWrite(destination, memberName, reviewed, notes);
            var section = RecordMap(source, destination);
            section.Rows[destinationName] =
                "| " + DestinationCell(destinationName, destinationSymbol) + " | " + description + " | " + string.Join(", ", notes) + " |";
            section.Cells[memberName] = new KeyValuePair<string, string>(description, string.Join(", ", notes));
            if (section.Method != null) WriteSet(section.Method).Add(memberName);
            _edges.Add(new Edge(section.Method != null ? MethodDisplay(section.Method) : "(nested)", Short(source), sourcePath,
                Short(destination.WithNullableAnnotation(NullableAnnotation.NotAnnotated)), memberName, string.Join(", ", notes)));
        }

        /// <summary>One data flow, for the CSV, JSON and Mermaid exports.</summary>
        private readonly struct Edge
        {
            public Edge(string method, string sourceType, string sourcePath, string destinationType, string destinationMember, string notes)
            {
                Method = method;
                SourceType = sourceType;
                SourcePath = sourcePath;
                DestinationType = destinationType;
                DestinationMember = destinationMember;
                Notes = notes;
            }

            public string Method { get; }
            public string SourceType { get; }
            public string SourcePath { get; }
            public string DestinationType { get; }
            public string DestinationMember { get; }
            public string Notes { get; }
        }

        private readonly List<Edge> _edges = new List<Edge>();

        // ------------------------------------------------------------------ CM0026: members that look like personal data

        private static readonly string[] PersonalNames =
        {
            "ssn", "socialsecurity", "taxid", "tin", "nationalid", "dateofbirth", "birthdate", "dob", "email", "phone", "mobile", "fax",
            "bankaccount", "accountnumber", "routingnumber", "iban", "creditcard", "cardnumber", "cvv", "passport", "driverslicense",
            "driverlicense", "licensenumber", "password", "passwordhash", "secret", "apikey", "accesstoken", "refreshtoken",
        };

        // Hungarian-style prefixes common in database-first code (StrEmail, DtDateOfBirth).
        private static readonly string[] TypePrefixes = { "str", "int", "dec", "dt", "lng", "bit", "sz", "s" };

        private readonly HashSet<ISymbol> _suggestedSensitive = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        /// <summary>CM0026 (info) on a mapped member, declared in this compilation, whose name looks like personal data.</summary>
        private void SuggestSensitive(ISymbol member)
        {
            if (!(member is IPropertySymbol || member is IFieldSymbol) || MemberResolver.IsSensitive(member)) return;
            var location = member.Locations.FirstOrDefault();
            if (location == null || !location.IsInSource || !_suggestedSensitive.Add(member)) return;
            var type = MemberResolver.TypeOf(member);
            if (MemberResolver.IsNullableValueType(type, out var underlying)) type = underlying;
            var name = member.Name.ToLowerInvariant();
            var isText = type.SpecialType == SpecialType.System_String;
            var isDate = type.SpecialType == SpecialType.System_DateTime || type.ToDisplayString() == "System.DateOnly";
            if (!isText && !isDate) return;
            foreach (var prefix in TypePrefixes)
                if (name.Length > prefix.Length + 2 && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(member.Name[prefix.Length]))
                {
                    name = name.Substring(prefix.Length);
                    break;
                }
            var matches = PersonalNames.Any(p => name == p || (p.Length > 3 && (name.EndsWith(p, StringComparison.Ordinal) || name.StartsWith(p, StringComparison.Ordinal))));
            if (!matches || (isDate && !(name.Contains("birth") || name == "dob"))) return;
            _diagnostics.Add(Diagnostic.Create(Diagnostics.SuggestSensitive, location, (member.ContainingType != null ? Short(member.ContainingType) + "." : string.Empty) + member.Name));
        }

        // ------------------------------------------------------------------ lineage and source reads

        /// <summary>Mapping method -> destination member -> source member path (plain copies only, no conversion methods).</summary>
        private readonly Dictionary<IMethodSymbol, SortedDictionary<string, string>> _lineage = new Dictionary<IMethodSymbol, SortedDictionary<string, string>>(SymbolEqualityComparer.Default);

        /// <summary>Source type -> its members that some map of this class reads.</summary>
        private readonly Dictionary<ITypeSymbol, HashSet<string>> _reads = new Dictionary<ITypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);

        /// <summary>Source types mapped at the root of a method (checked for unread members).</summary>
        private readonly HashSet<ITypeSymbol> _rootSources = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        private void RecordLineage(ITypeSymbol source, ITypeSymbol destination, string memberName, string path)
        {
            if (_current == null || _rootSource == null || _rootDestination == null ||
                !SymbolEqualityComparer.Default.Equals(source, _rootSource) || !SymbolEqualityComparer.Default.Equals(destination, _rootDestination)) return;
            if (!_lineage.TryGetValue(_current, out var map))
            {
                map = new SortedDictionary<string, string>(StringComparer.Ordinal);
                _lineage[_current] = map;
            }
            map[memberName] = path;
        }

        private void RecordRead(ISymbol member)
        {
            if (member.ContainingType == null) return;
            var type = member.ContainingType.OriginalDefinition;
            if (!_reads.TryGetValue(type, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                _reads[type] = names;
            }
            names.Add(member.Name);
        }

        /// <summary>"firstName (constructor)" -> "FirstName" when the destination has a member with that name.</summary>
        private string DestinationMemberName(ITypeSymbol destination, string destinationName)
        {
            var name = destinationName.Replace(" (constructor)", string.Empty);
            if (destination is INamedTypeSymbol named)
                foreach (var member in GetWritableMembers(named, false).Concat(_resolver.GetReadableMembers(named)))
                    if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)) return member.Name;
            return name;
        }

        /// <summary>
        /// A real key: a member marked [Key] or [ForeignKey], named by [PrimaryKey] or by a navigation's [ForeignKey], or, when
        /// the destination is an EF Core entity, a key by EF's conventions (Id, &lt;Type&gt;Id, &lt;Navigation&gt;Id,
        /// &lt;PrincipalType&gt;Id). Names alone (ErrorObjectId, RecordID on a DTO) are not keys.
        /// </summary>
        private bool IsKey(ITypeSymbol owner, string memberName)
        {
            var types = SelfAndBases(owner).ToList();
            foreach (var type in types)
            {
                foreach (var member in type.GetMembers(memberName))
                    if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "KeyAttribute" || a.AttributeClass?.Name == "ForeignKeyAttribute")) return true;
                foreach (var attribute in type.GetAttributes())
                    if (attribute.AttributeClass?.Name == "PrimaryKeyAttribute" && StringArguments(attribute).Any(n => string.Equals(n, memberName, StringComparison.Ordinal)))
                        return true;
                foreach (var navigation in type.GetMembers().OfType<IPropertySymbol>())
                    foreach (var attribute in navigation.GetAttributes())
                        if (attribute.AttributeClass?.Name == "ForeignKeyAttribute" && StringArguments(attribute).Any(n => string.Equals(n, memberName, StringComparison.Ordinal)))
                            return true;
            }

            if (Ef.Keys(owner) is IReadOnlyCollection<string> configuredKeys && configuredKeys.Contains(memberName)) return true;
            if (Ef.IsForeignKey(owner, memberName)) return true;
            if (!memberName.EndsWith("id", StringComparison.OrdinalIgnoreCase) || !IsEntity(owner)) return false;
            if (string.Equals(memberName, "Id", StringComparison.OrdinalIgnoreCase) || string.Equals(memberName, owner.Name + "Id", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var type in types)
                foreach (var navigation in type.GetMembers().OfType<IPropertySymbol>())
                    if (IsEntity(navigation.Type) &&
                        (string.Equals(memberName, navigation.Name + "Id", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(memberName, navigation.Type.Name + "Id", StringComparison.OrdinalIgnoreCase)))
                        return true;
            return false;
        }

        private static IEnumerable<ITypeSymbol> SelfAndBases(ITypeSymbol type)
        {
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
                yield return t;
        }

        private EfModel? _efModel;

        private EfModel Ef => _efModel ??= EfModel.Build(_compilation, _ct);

        private bool IsEntity(ITypeSymbol type) => Ef.IsEntity(type);

        /// <summary>Maximum string length of a member: [MaxLength], [StringLength] or HasMaxLength in the EF model.</summary>
        private int? MaxLength(ITypeSymbol owner, string memberName)
        {
            foreach (var type in SelfAndBases(owner))
                foreach (var member in type.GetMembers(memberName))
                    foreach (var attribute in member.GetAttributes())
                    {
                        var name = attribute.AttributeClass?.Name;
                        if ((name == "MaxLengthAttribute" || name == "StringLengthAttribute") && attribute.ConstructorArguments.Length > 0 &&
                            attribute.ConstructorArguments[0].Value is int length && length > 0)
                            return length;
                    }
            return Ef.MaxLength(owner, memberName);
        }

        /// <summary>
        /// A key the database generates: [DatabaseGenerated(Identity or Computed)], a generated value in the EF model, or the
        /// single integer or Guid primary key of an entity (EF generates those by default).
        /// </summary>
        private bool IsDatabaseGenerated(ITypeSymbol owner, string memberName)
        {
            foreach (var type in SelfAndBases(owner))
                foreach (var member in type.GetMembers(memberName))
                    foreach (var attribute in member.GetAttributes())
                        if (attribute.AttributeClass?.Name == "DatabaseGeneratedAttribute" && attribute.ConstructorArguments.Length == 1 &&
                            attribute.ConstructorArguments[0].Value is int option)
                            return option != 0; // DatabaseGeneratedOption.None = 0
            var configured = Ef.IsGenerated(owner, memberName);
            if (configured.HasValue) return configured.Value;
            if (!IsEntity(owner)) return false;

            var keys = Ef.Keys(owner);
            var isSingleKey = keys != null
                ? keys.Count == 1 && keys.Contains(memberName)
                : string.Equals(memberName, "Id", StringComparison.OrdinalIgnoreCase) || string.Equals(memberName, owner.Name + "Id", StringComparison.OrdinalIgnoreCase);
            var attributedKeys = SelfAndBases(owner).SelectMany(t => t.GetMembers())
                .Where(m => m.GetAttributes().Any(a => a.AttributeClass?.Name == "KeyAttribute")).Select(m => m.Name).ToList();
            if (attributedKeys.Count > 0) isSingleKey = attributedKeys.Count == 1 && attributedKeys[0] == memberName;
            if (!isSingleKey) return false; // composite keys are not generated by default
            var type0 = SelfAndBases(owner).SelectMany(t => t.GetMembers(memberName)).Select(MemberType).FirstOrDefault();
            if (type0 == null) return false;
            if (MemberResolver.IsNullableValueType(type0, out var underlying)) type0 = underlying;
            switch (type0.SpecialType)
            {
                case SpecialType.System_Int16:
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                    return true;
            }
            return type0.ToDisplayString() == "System.Guid";
        }

        private static ITypeSymbol? MemberType(ISymbol member) =>
            member is IPropertySymbol p ? p.Type : member is IFieldSymbol f ? f.Type : null;

        /// <summary>Navigation of an entity: a member whose type (or element type) is an entity.</summary>
        private bool IsNavigation(ITypeSymbol owner, string memberName)
        {
            var type = SelfAndBases(owner).SelectMany(t => t.GetMembers(memberName)).Select(MemberType).FirstOrDefault();
            if (type == null) return false;
            var target = EfModel.ElementType(type) ?? type;
            return IsEntity(target);
        }

        private readonly HashSet<string> _reportedDatabaseWrites = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>CM0028: create and update methods writing a database-generated key or a navigation of an entity.</summary>
        private void CheckDatabaseWrite(ITypeSymbol destination, string memberName, bool reviewed, List<string> notes)
        {
            if (_current == null || TryGetProjection(_current, out _, out _, out _) || !IsEntity(destination)) return;
            string? what = null;
            if (IsDatabaseGenerated(destination, memberName)) what = "the database-generated key";
            else if (IsNavigation(destination, memberName)) what = "the navigation";
            if (what == null) return;
            if (reviewed)
            {
                notes.Add(what == "the navigation" ? "navigation (reviewed)" : "generated key (reviewed)");
                return;
            }
            notes.Add(what == "the navigation" ? "⚠ navigation" : "⚠ generated key");
            _riskyFlows.Add("`" + Short(destination) + "." + memberName + "` is " + what.Substring(4) + " of an entity");
            if (_reportedDatabaseWrites.Add(_current.Name + "|" + T(destination) + "|" + memberName))
                Report(Diagnostics.DatabaseGeneratedWrite, _current.Name, what, memberName, Short(destination.WithNullableAnnotation(NullableAnnotation.NotAnnotated)));
        }

        private SortedSet<string> WriteSet(IMethodSymbol method)
        {
            if (!_writeSets.TryGetValue(method, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                _writeSets[method] = set;
            }
            return set;
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

        /// <summary>Readable members of each root source type that no map of this class reads (only data members, not Get-methods).</summary>
        private SortedDictionary<string, List<string>> UnreadSourceMembers()
        {
            var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var type in _rootSources)
            {
                var unread = _resolver.GetReadableMembers(type)
                    .Where(m => !(m is IMethodSymbol))
                    .Where(m => !(m.ContainingType != null && _reads.TryGetValue(m.ContainingType.OriginalDefinition, out var names) && names.Contains(m.Name)))
                    .Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (unread.Count > 0) result[Short(type)] = unread;
            }
            return result;
        }

        /// <summary>The flows as CSV lines (no header): Mapper,Method,SourceType,SourceMember,DestinationType,DestinationMember,Notes.</summary>
        private string BuildCsv()
        {
            var mapper = _mapper.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var sb = new StringBuilder();
            foreach (var edge in _edges.Distinct())
                sb.Append(Csv(mapper)).Append(',').Append(Csv(edge.Method)).Append(',').Append(Csv(edge.SourceType)).Append(',').Append(Csv(edge.SourcePath))
                  .Append(',').Append(Csv(edge.DestinationType)).Append(',').Append(Csv(edge.DestinationMember)).Append(',').Append(Csv(edge.Notes)).Append('\n');
            return sb.ToString();
        }

        private static string Csv(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        /// <summary>This mapper's flows as a JSON object (for tools such as a pull-request summary).</summary>
        private string BuildJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"mapper\":").Append(Json(_mapper.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))).Append(",\"maps\":[");
            var first = true;
            foreach (var section in _flows.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"map\":").Append(Json(section.Method != null ? MethodDisplay(section.Method) : section.Heading.Replace("`", string.Empty)))
                  .Append(",\"destination\":").Append(Json(section.Destination)).Append(",\"members\":[");
                var firstMember = true;
                foreach (var cell in section.Cells)
                {
                    if (!firstMember) sb.Append(',');
                    firstMember = false;
                    sb.Append("{\"member\":").Append(Json(cell.Key)).Append(",\"source\":").Append(Json(cell.Value.Key.Trim('`')))
                      .Append(",\"notes\":").Append(Json(cell.Value.Value)).Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("],\"unreadSourceMembers\":{");
            var firstType = true;
            foreach (var pair in UnreadSourceMembers())
            {
                if (!firstType) sb.Append(',');
                firstType = false;
                sb.Append(Json(pair.Key)).Append(":[").Append(string.Join(",", pair.Value.Select(Json))).Append(']');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Json(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>The Markdown section for this mapper class (combined per assembly into ConventionMapper.Generated.DataFlowReport).</summary>
        private string? BuildReport()
        {
            if (_flows.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append("## `").Append(_mapper.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)).AppendLine("`").AppendLine();

            if (_riskyFlows.Count > 0)
            {
                sb.AppendLine("Review first:").AppendLine();
                foreach (var risky in _riskyFlows) sb.Append("- ").AppendLine(risky);
                sb.AppendLine();
            }

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

            if (_conversionUses.Count > 0)
            {
                sb.AppendLine("Conversion methods:").AppendLine();
                sb.AppendLine("| Method | Converts | Used for |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var pair in _conversionUses.OrderBy(p => p.Key.Name, StringComparer.Ordinal).ThenBy(p => MethodDisplay(p.Key), StringComparer.Ordinal))
                {
                    var marked = _markedConversions.Contains(pair.Key) ? " ([MapConversion])" : IsPartialDefinition(pair.Key) ? " (mapping method)" : string.Empty;
                    sb.Append("| `").Append(pair.Key.Name).Append("()`").Append(marked)
                      .Append(" | ").Append(Short(pair.Key.Parameters[0].Type)).Append(" → ").Append(Short(pair.Key.ReturnType))
                      .Append(" | ").Append(string.Join(", ", pair.Value.Select(v => "`" + v + "`"))).AppendLine(" |");
                }
                sb.AppendLine();
            }

            var unread = UnreadSourceMembers();
            if (unread.Count > 0)
            {
                sb.AppendLine("Source members no map reads (dead data, or a forgotten mapping?):").AppendLine();
                foreach (var pair in unread) sb.Append("- `").Append(pair.Key).Append("`: ").AppendLine(string.Join(", ", pair.Value.Select(m => "`" + m + "`")));
                sb.AppendLine();
            }

            foreach (var group0 in _flows.Values.Where(f => f.Method != null).GroupBy(f => f.Destination).Where(g => g.Count() > 1))
            {
                var siblings = group0.ToList();
                sb.Append("Maps to `").Append(group0.Key).AppendLine("` side by side:").AppendLine();
                sb.Append("| Member |");
                foreach (var sibling in siblings) sb.Append(" `").Append(MethodDisplay(sibling.Method!)).Append("` |");
                sb.AppendLine();
                sb.Append("| --- |");
                foreach (var _ in siblings) sb.Append(" --- |");
                sb.AppendLine();
                foreach (var member in siblings.SelectMany(f => f.Cells.Keys).Distinct().OrderBy(m => m, StringComparer.Ordinal))
                {
                    sb.Append("| ").Append(member).Append(" |");
                    foreach (var sibling in siblings)
                        sb.Append(' ').Append(sibling.Cells.TryGetValue(member, out var cell) ? (cell.Key == "—" ? cell.Value : cell.Key) : "—").Append(" |");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            string? group = null;
            var nestedStarted = false;
            foreach (var pair in _flows)
            {
                var section = pair.Value;
                if (section.Method != null && section.Method.Name != group)
                {
                    group = section.Method.Name;
                    sb.Append("### ").AppendLine(group).AppendLine();
                }
                else if (section.Method == null && !nestedStarted)
                {
                    nestedStarted = true;
                    sb.AppendLine("### Nested maps").AppendLine();
                }
                sb.Append("#### ").AppendLine(section.Heading).AppendLine();
                if (section.Rows.Count == 0)
                {
                    sb.AppendLine("Converted as a whole (no member-by-member flows).").AppendLine();
                    continue;
                }
                sb.AppendLine("| Destination | Source | Notes |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var row in section.Rows.Values) sb.AppendLine(row);
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

        private void ReportAt(DiagnosticDescriptor descriptor, Location location, params object[] args) =>
            _diagnostics.Add(Diagnostic.Create(descriptor, location, args));

        private sealed class MemberConfig
        {
            public static readonly MemberConfig Empty = new MemberConfig();

            public HashSet<string> Ignored { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapIgnore] on the method itself (not inherited through [MapInherit]).</summary>
            public HashSet<string> OwnIgnored { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Explicit { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapExplicit]: only listed, [MapMember] and parameter-supplied members are set.</summary>
            public bool ExplicitMode { get; set; }
            public HashSet<string> Listed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, TypedConstant> NullSubstitutes { get; } = new Dictionary<string, TypedConstant>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Extra parameters of the mapping method, by name.</summary>
            public Dictionary<string, IParameterSymbol> Parameters { get; } = new Dictionary<string, IParameterSymbol>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UsedParameters { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>[MapRedact]: [Sensitive] members are masked, also in nested objects and collections.</summary>
            public bool Redact { get; set; }

            /// <summary>[MapConstant]: destination member -> constant.</summary>
            public Dictionary<string, TypedConstant> Constants { get; } = new Dictionary<string, TypedConstant>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapFrom]: destination member -> name of the static method that computes it.</summary>
            public Dictionary<string, string> Computed { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapRequired]: destination members whose source must not be null.</summary>
            public HashSet<string> Required { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapTruncate]: destination member -> maximum length (0: from the destination's attributes or EF model).</summary>
            public Dictionary<string, int> Truncate { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapReviewed]: key members whose writes have been reviewed (left out of "Review first").</summary>
            public HashSet<string> Reviewed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapMember] destinations whose path starts with the source parameter's name ("source.CreateUser").</summary>
            public HashSet<string> SourcePrefixed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>[MapAllowParse]: destination members allowed to use parsing or narrowing conversions.</summary>
            public HashSet<string> AllowParse { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool Includes(string member) =>
                !ExplicitMode || Listed.Contains(member) || Explicit.ContainsKey(member) || Parameters.ContainsKey(member) ||
                Constants.ContainsKey(member) || Computed.ContainsKey(member);
        }

        /// <summary>What converting one value did: conversions that can fail or lose data, and the user methods called.</summary>
        private sealed class Trace
        {
            public List<string> Risks { get; } = new List<string>();

            /// <summary>Projection-only: parts that may not translate to SQL (methods are tracked in <see cref="Methods"/>).</summary>
            public List<string> ClientRisks { get; } = new List<string>();
            public List<IMethodSymbol> Methods { get; } = new List<IMethodSymbol>();

            public void MergeFrom(Trace other)
            {
                Risks.AddRange(other.Risks);
                ClientRisks.AddRange(other.ClientRisks);
                Methods.AddRange(other.Methods);
            }
        }

        /// <summary>Runs a value conversion with a fresh trace, so its findings belong to one destination member.</summary>
        private string? Traced(Func<string?> convert, out Trace trace)
        {
            var outer = _trace;
            var inner = new Trace();
            _trace = inner;
            try
            {
                return convert();
            }
            finally
            {
                _trace = outer;
                trace = inner;
            }
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

        public CodeWriter Open(string? header)
        {
            if (header != null) Line(header);
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
