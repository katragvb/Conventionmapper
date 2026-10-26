using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>
    /// Writes mapping code for partial methods declared in classes marked [GeneratedMapper].
    /// The generated code is plain C#: no reflection, no runtime dependency, Native AOT and trimming safe.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class MapperGenerator : IIncrementalGenerator
    {
        public const string GeneratedMapperAttributeName = "ConventionMapper.GeneratedMapperAttribute";
        public const string MapMemberAttributeName = "ConventionMapper.MapMemberAttribute";
        public const string MapIgnoreAttributeName = "ConventionMapper.MapIgnoreAttribute";
        public const string MapExplicitAttributeName = "ConventionMapper.MapExplicitAttribute";
        public const string MapNullSubstituteAttributeName = "ConventionMapper.MapNullSubstituteAttribute";
        public const string MapAllowParseAttributeName = "ConventionMapper.MapAllowParseAttribute";
        public const string MapConversionAttributeName = "ConventionMapper.MapConversionAttribute";
        public const string MapInheritAttributeName = "ConventionMapper.MapInheritAttribute";
        public const string MapReviewedAttributeName = "ConventionMapper.MapReviewedAttribute";
        public const string MapConstantAttributeName = "ConventionMapper.MapConstantAttribute";
        public const string MapFromAttributeName = "ConventionMapper.MapFromAttribute";
        public const string MapRequiredAttributeName = "ConventionMapper.MapRequiredAttribute";
        public const string MapTruncateAttributeName = "ConventionMapper.MapTruncateAttribute";
        public const string MapMergeKeyAttributeName = "ConventionMapper.MapMergeKeyAttribute";
        public const string MapRedactAttributeName = "ConventionMapper.MapRedactAttribute";
        public const string MapLogStringAttributeName = "ConventionMapper.MapLogStringAttribute";
        public const string MapPatchAttributeName = "ConventionMapper.MapPatchAttribute";
        public const string MapCombineAttributeName = "ConventionMapper.MapCombineAttribute";
        public const string MapPreferAttributeName = "ConventionMapper.MapPreferAttribute";
        public const string MapWhenAttributeName = "ConventionMapper.MapWhenAttribute";
        public const string MapCloneAttributeName = "ConventionMapper.MapCloneAttribute";
        public const string MapEqualityAttributeName = "ConventionMapper.MapEqualityAttribute";
        public const string MapPreserveReferencesAttributeName = "ConventionMapper.MapPreserveReferencesAttribute";
        public const string MapFormatAttributeName = "ConventionMapper.MapFormatAttribute";
        public const string MapCheckedAttributeName = "ConventionMapper.MapCheckedAttribute";
        public const string MapValidateAttributeName = "ConventionMapper.MapValidateAttribute";
        public const string MapUseAttributeName = "ConventionMapper.MapUseAttribute";
        public const string GeneratedBuilderAttributeName = "ConventionMapper.GeneratedBuilderAttribute";
        public const string FieldChangeName = "ConventionMapper.FieldChange";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(static ctx =>
                ctx.AddSource("ConventionMapper.Attributes.g.cs", Text(AttributeSource)));

            var mappers = context.SyntaxProvider.ForAttributeWithMetadataName(
                GeneratedMapperAttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => MapperEmitter.Run((INamedTypeSymbol)ctx.TargetSymbol, (TypeDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel.Compilation, ct));

            // <ConventionMapperGeneratedDocs>true</ConventionMapperGeneratedDocs>: XML docs on generated methods listing each member's source.
            var docs = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.ConventionMapperGeneratedDocs", out var value) &&
                string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

            // Generated members get [GeneratedCode], and [ExcludeFromCodeCoverage] unless
            // <ConventionMapperExcludeFromCodeCoverage>false</ConventionMapperExcludeFromCodeCoverage> (when the target framework has them).
            var excludeCoverage = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                !(options.GlobalOptions.TryGetValue("build_property.ConventionMapperExcludeFromCodeCoverage", out var value) &&
                  string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)));
            var attributes = context.CompilationProvider.Select(static (compilation, _) =>
                (compilation.GetTypeByMetadataName("System.CodeDom.Compiler.GeneratedCodeAttribute") != null,
                 compilation.GetTypeByMetadataName("System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute") != null));
            var memberOptions = excludeCoverage.Combine(attributes).Select(static (pair, _) =>
                new MemberAttributes(pair.Right.Item1, pair.Left && pair.Right.Item2));

            context.RegisterSourceOutput(mappers.Combine(docs).Combine(memberOptions), static (spc, pair) =>
            {
                var result = pair.Left.Left;
                foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
                if (result.Source != null)
                    spc.AddSource(result.HintName, Text(ApplyMemberAttributes(ApplyDocs(result.Source, pair.Left.Right), pair.Right)));
            });

            // One data-flow report per assembly, with a section per mapper class.
            var reports = mappers
                .Select(static (result, _) => result.Report == null ? null : new ReportSection(result.MapperName, result.Report, result.Csv ?? string.Empty, result.Json ?? "{}"))
                .Collect();
            // The snapshot helpers use System.IO; skip them for targets without it (netstandard1.x).
            var fileApi = context.CompilationProvider.Select(static (compilation, _) =>
                compilation.GetTypeByMetadataName("System.IO.File") != null &&
                compilation.GetTypeByMetadataName("System.AppDomain") != null &&
                compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.CallerFilePathAttribute") != null);
            context.RegisterSourceOutput(reports.Combine(fileApi), static (spc, pair) =>
            {
                var parts = pair.Left.Where(s => s != null).Select(s => s!).OrderBy(s => s.Mapper, StringComparer.Ordinal).ToList();
                if (parts.Count == 0) return;
                spc.AddSource("ConventionMapper.DataFlowReport.g.cs", Text(DataFlowReportSource.Build(parts, pair.Right)));
            });

            // CM0024: the IDE keeps an old generator loaded after a package upgrade. The package's props record the version
            // the project references; a different running version means the generated code is stale.
            var expectedVersion = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.ConventionMapperSourceGeneratorVersion", out var value) ? value?.Trim() ?? string.Empty : string.Empty);
            context.RegisterSourceOutput(expectedVersion, static (spc, expected) =>
            {
                // Only real versions (the props template in the repository carries a placeholder).
                if (expected.Length == 0 || !char.IsDigit(expected[0])) return;
                var running = RunningVersion();
                if (running.Length > 0 && !string.Equals(running, expected, StringComparison.OrdinalIgnoreCase))
                    spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.StaleGenerator, Location.None, running, expected));
            });
        }

        /// <summary>Generated files always use "\n" line endings, whatever the line endings of this generator's own source were.</summary>
        private static SourceText Text(string source) => SourceText.From(source.Replace("\r\n", "\n").Replace('\r', '\n'), Encoding.UTF8);

        /// <summary>This generator's package version ("1.9.0"), from its informational version without build metadata.</summary>
        internal static string RunningVersion()
        {
            var attribute = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                typeof(MapperGenerator).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var version = attribute?.InformationalVersion ?? string.Empty;
            var plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        /// <summary>Which attributes generated members get.</summary>
        internal readonly struct MemberAttributes : IEquatable<MemberAttributes>
        {
            public MemberAttributes(bool generatedCode, bool excludeFromCoverage)
            {
                GeneratedCode = generatedCode;
                ExcludeFromCoverage = excludeFromCoverage;
            }

            public bool GeneratedCode { get; }
            public bool ExcludeFromCoverage { get; }
            public bool Equals(MemberAttributes other) => GeneratedCode == other.GeneratedCode && ExcludeFromCoverage == other.ExcludeFromCoverage;
            public override bool Equals(object? obj) => obj is MemberAttributes other && Equals(other);
            public override int GetHashCode() => (GeneratedCode ? 1 : 0) + (ExcludeFromCoverage ? 2 : 0);
        }

        /// <summary>Replaces the emitter's member markers with [GeneratedCode] and [ExcludeFromCodeCoverage], or removes them.</summary>
        internal static string ApplyMemberAttributes(string source, MemberAttributes options)
        {
            if (source.IndexOf(MapperEmitter.MemberMarker, StringComparison.Ordinal) < 0) return source;
            var attribute = (options.GeneratedCode ? "[global::System.CodeDom.Compiler.GeneratedCode(\"ConventionMapper.SourceGenerator\", \"" + RunningVersion() + "\")]" : string.Empty) +
                            (options.ExcludeFromCoverage ? "[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]" : string.Empty);
            var lines = source.Split('\n');
            var sb = new StringBuilder(source.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var marker = line.IndexOf(MapperEmitter.MemberMarker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    if (attribute.Length == 0) continue;
                    line = line.Substring(0, marker) + attribute;
                }
                sb.Append(line);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Turns the emitter's doc markers into "///" lines, or removes them.</summary>
        internal static string ApplyDocs(string source, bool includeDocs)
        {
            if (source.IndexOf(MapperEmitter.DocMarker, StringComparison.Ordinal) < 0) return source;
            var lines = source.Split('\n');
            var sb = new StringBuilder(source.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var marker = line.IndexOf(MapperEmitter.DocMarker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    if (!includeDocs) continue;
                    line = line.Substring(0, marker) + "/// " + line.Substring(marker + MapperEmitter.DocMarker.Length);
                }
                sb.Append(line);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        private const string AttributeSource = @"// <auto-generated/>
#nullable enable
namespace ConventionMapper
{
    /// <summary>
    /// Marks a partial class whose partial mapping methods are implemented at build time by ConventionMapper.SourceGenerator.
    /// <code>
    /// [GeneratedMapper]
    /// public static partial class OrderMapper
    /// {
    ///     public static partial OrderDto ToDto(this Order source);
    ///     public static partial void Update(OrderDto source, Order target);
    /// }
    /// </code>
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class GeneratedMapperAttribute : global::System.Attribute
    {
        /// <summary>
        /// When true, only static methods marked [MapConversion] are used as conversions. By default every one-argument
        /// static method of the class is used (warning CM0017 for the ones not marked).
        /// </summary>
        public bool RequireMapConversion { get; set; }

        /// <summary>
        /// The LINQ provider the projections of this class run on: ""SqlServer"", ""PostgreSql"", ""Sqlite"", ""Cosmos"" or ""InMemory"".
        /// Tunes the projection warnings (CM0021) to what that provider translates.
        /// </summary>
        public string? Provider { get; set; }
    }

    /// <summary>Maps a source member or dotted path (for example ""Customer.Address.City"") to a destination member.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapMemberAttribute : global::System.Attribute
    {
        public MapMemberAttribute(string source, string destination)
        {
            Source = source;
            Destination = destination;
        }

        public string Source { get; }
        public string Destination { get; }
    }

    /// <summary>Leaves a destination member (or constructor parameter) out of the generated mapping.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapIgnoreAttribute : global::System.Attribute
    {
        public MapIgnoreAttribute(string destination)
        {
            Destination = destination;
        }

        public string Destination { get; }
    }

    /// <summary>
    /// Explicit mode: only the destination members named here, in [MapMember] or by an extra method parameter are set;
    /// every other member keeps its default value, so a member added later is never copied silently.
    /// On a class, applies to every mapping method in it.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapExplicitAttribute : global::System.Attribute
    {
        public MapExplicitAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }

    /// <summary>The value a destination member (or constructor parameter) gets when its source is null: [MapNullSubstitute(""Status"", """")].</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapNullSubstituteAttribute : global::System.Attribute
    {
        public MapNullSubstituteAttribute(string destination, object value)
        {
            Destination = destination;
            Value = value;
        }

        public string Destination { get; }
        public object Value { get; }
    }
    /// <summary>
    /// Allows parsing (<c>int.Parse</c>, <c>Enum.Parse</c>) and narrowing (<c>checked</c> casts) conversions for the named
    /// destination members, which are otherwise warning CM0016. On a class, applies to every method in it.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapAllowParseAttribute : global::System.Attribute
    {
        public MapAllowParseAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }

    /// <summary>
    /// Marks a static method of the mapper class that is meant to be used as a conversion wherever its parameter and return
    /// types meet, for example <c>[MapConversion] private static string Format(Money m)</c>.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapConversionAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Reuses the configuration ([MapMember], [MapIgnore], [MapExplicit], [MapNullSubstitute], [MapAllowParse], [MapReviewed])
    /// of another mapping method of this class with the same source and destination types, for example an overload with extra
    /// parameters: <c>[MapInherit(nameof(ToDto))]</c>. When several overloads qualify, name the one to use by its parameter
    /// types: <c>[MapInherit(nameof(ToDto), typeof(Order))]</c>. Attributes on this method are added to (and win over) the inherited ones.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapInheritAttribute : global::System.Attribute
    {
        public MapInheritAttribute(string method, params global::System.Type[] parameterTypes)
        {
            Method = method;
            ParameterTypes = parameterTypes;
        }

        public string Method { get; }
        public global::System.Type[] ParameterTypes { get; }
    }

    /// <summary>
    /// Marks key members (Id, foreign keys) whose writes in this method, or in every method of the class, have been reviewed,
    /// so they leave the ""Review first"" list of the data-flow report: <c>[MapReviewed(nameof(Policy.PolicyId))]</c>.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapReviewedAttribute : global::System.Attribute
    {
        public MapReviewedAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }
    /// <summary>A constant for a destination member (or constructor parameter): <c>[MapConstant(""Status"", FormStatus.New)]</c>.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapConstantAttribute : global::System.Attribute
    {
        public MapConstantAttribute(string destination, object value)
        {
            Destination = destination;
            Value = value;
        }

        public string Destination { get; }
        public object Value { get; }
    }

    /// <summary>
    /// Computes one destination member with a static method of the mapper class that takes the source (and, after it, any of
    /// the mapping method's extra parameters by name): <c>[MapFrom(nameof(TruncateCity), ""City"")]</c>. In projections an
    /// expression-bodied method is inlined, so it translates to SQL.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapFromAttribute : global::System.Attribute
    {
        public MapFromAttribute(string method, string destination)
        {
            Method = method;
            Destination = destination;
        }

        public string Method { get; }
        public string Destination { get; }
    }

    /// <summary>
    /// The named destination members must have a value: when their source is null the mapping throws
    /// InvalidOperationException naming the member, instead of writing a default.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapRequiredAttribute : global::System.Attribute
    {
        public MapRequiredAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }

    /// <summary>
    /// Cuts a string to the destination's maximum length ([MaxLength], [StringLength] or the EF Core model), or to
    /// the given length: <c>[MapTruncate(""City"")]</c>, <c>[MapTruncate(""City"", 30)]</c>.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapTruncateAttribute : global::System.Attribute
    {
        public MapTruncateAttribute(string destination, int length = 0)
        {
            Destination = destination;
            Length = length;
        }

        public string Destination { get; }
        public int Length { get; }
    }
    /// <summary>
    /// Makes <c>void Merge(IEnumerable&lt;TSource&gt; source, ICollection&lt;TTarget&gt; target)</c> merge by key: items whose key
    /// matches are updated in place (with the mapper's Update(TSource, TTarget) method when it has one), new items are
    /// added, and target items without a match are removed (unless <see cref=""RemoveMissing""/> is false).
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapMergeKeyAttribute : global::System.Attribute
    {
        public MapMergeKeyAttribute(string sourceKey, string? targetKey = null)
        {
            SourceKey = sourceKey;
            TargetKey = targetKey ?? sourceKey;
        }

        public string SourceKey { get; }
        public string TargetKey { get; }

        /// <summary>Remove target items that have no source item with their key (default true).</summary>
        public bool RemoveMissing { get; set; } = true;
    }
    /// <summary>
    /// Makes <c>T Redact(T value)</c> return a copy with every [Sensitive] member masked (strings become ""***"", other values
    /// their default), including in nested objects and collections, for logging or support tools.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapRedactAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Makes <c>string ToLogString(T value)</c> write the value's members as text, ""Order { Id = 7, Ssn = *** }"", with every
    /// [Sensitive] member masked, so objects can be logged without leaking personal data.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapLogStringAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Makes an update method a PATCH: a member is copied only when the caller set it. A null source value (string?, int?)
    /// or an Optional&lt;T&gt;-shaped wrapper without a value (HasValue is false) leaves the target member unchanged.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapPatchAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Combines several sources into one object: <c>[MapCombine] Policy Combine(Policy entered, Policy saved, Policy defaults)</c>.
    /// Each member takes the first source, in parameter order, that has a value (not null; for value types, not the default).
    /// [MapPrefer] changes the order for single members.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapCombineAttribute : global::System.Attribute
    {
    }

    /// <summary>In a [MapCombine] method, the named members prefer this source parameter: <c>[MapPrefer(""saved"", ""City"")]</c>.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapPreferAttribute : global::System.Attribute
    {
        public MapPreferAttribute(string source, params string[] members)
        {
            Source = source;
            Members = members;
        }

        public string Source { get; }
        public string[] Members { get; }
    }

    /// <summary>
    /// Writes a destination member only when a static bool method of the mapper class (taking the source, then extra
    /// parameters by name) returns true: <c>[MapWhen(""Discount"", nameof(IsEligible))]</c>. Otherwise the member keeps its value.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapWhenAttribute : global::System.Attribute
    {
        public MapWhenAttribute(string destination, string predicate)
        {
            Destination = destination;
            Predicate = predicate;
        }

        public string Destination { get; }
        public string Predicate { get; }
    }

    /// <summary>Makes <c>T Clone(T value)</c> a deep copy: nested objects and collections are copied too, not shared.</summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapCloneAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Makes <c>IEqualityComparer&lt;T&gt; Comparer()</c> return a comparer over T's members (nested objects and collections
    /// compared by value), with [MapIgnore] for members to leave out: a ""did anything change?"" check before saving.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapEqualityAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Keeps shared references and cycles: an object reached twice in the source maps to one destination object, and a cycle
    /// does not recurse forever. Costs a dictionary lookup per object.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapPreserveReferencesAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Formats or parses a member with a fixed format in the invariant culture: <c>[MapFormat(""EffectiveDate"", ""yyyy-MM-dd"")]</c>
    /// writes <c>ToString(""yyyy-MM-dd"", CultureInfo.InvariantCulture)</c>, or <c>ParseExact</c> from a string.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapFormatAttribute : global::System.Attribute
    {
        public MapFormatAttribute(string destination, string format)
        {
            Destination = destination;
            Format = format;
        }

        public string Destination { get; }
        public string Format { get; }
    }

    /// <summary>
    /// Conversions that can lose data (long to int, decimal to double, DateTime to DateOnly) are checked for the named members:
    /// a value out of range, or one that would lose its time part, throws OverflowException naming the member.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapCheckedAttribute : global::System.Attribute
    {
        public MapCheckedAttribute(params string[] members)
        {
            Members = members;
        }

        public string[] Members { get; }
    }

    /// <summary>
    /// Makes <c>IReadOnlyList&lt;(string Member, string Message)&gt; Validate(T value)</c> check T against what the generator
    /// knows: maximum lengths ([MaxLength], [StringLength], the EF model), [Required] and non-nullable reference members,
    /// and [Range]. An empty list means valid.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapValidateAttribute : global::System.Attribute
    {
    }

    /// <summary>
    /// Nested members also use the public static mapping methods of another class, such as shared maps in a referenced
    /// project: <c>[MapUse(typeof(SharedMappings))]</c>. The mapper's own methods win.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapUseAttribute : global::System.Attribute
    {
        public MapUseAttribute(global::System.Type mappings)
        {
            Mappings = mappings;
        }

        public global::System.Type Mappings { get; }
    }

    /// <summary>
    /// Generates a fluent builder for a type on a partial class: <c>[GeneratedBuilder(typeof(Order))] partial class OrderBuilder { }</c>
    /// gets <c>WithStatus(...)</c> for each settable member and <c>Build()</c>. Start from an existing object with the
    /// constructor that takes one (SampleData.Create&lt;Order&gt;() in tests).
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class GeneratedBuilderAttribute : global::System.Attribute
    {
        public GeneratedBuilderAttribute(global::System.Type type)
        {
            Type = type;
        }

        public global::System.Type Type { get; }
    }

    /// <summary>One member that differs, returned by a generated <c>IReadOnlyList&lt;FieldChange&gt; Diff(TBefore before, TAfter after)</c> method.</summary>
    internal sealed class FieldChange
    {
        public FieldChange(string member, object? before, object? after)
        {
            Member = member;
            Before = before;
            After = after;
        }

        /// <summary>The member of the ""after"" type.</summary>
        public string Member { get; }

        /// <summary>The old value (""***"" for [Sensitive] members).</summary>
        public object? Before { get; }

        /// <summary>The new value (""***"" for [Sensitive] members).</summary>
        public object? After { get; }

        public override string ToString() => Member + "": "" + (Before ?? ""null"") + "" -> "" + (After ?? ""null"");
    }
#if !CONVENTIONMAPPER_RUNTIME
    /// <summary>
    /// Marks a member that must never be copied into a destination member that is not also [Sensitive]
    /// (password hashes, tokens, personal data). A mapping that would do so is build error CM0009.
    /// When the ConventionMapper runtime package is referenced, its public attribute is used instead.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Property | global::System.AttributeTargets.Field | global::System.AttributeTargets.Parameter, Inherited = true)]
    internal sealed class SensitiveAttribute : global::System.Attribute
    {
    }
#endif
}
";
    }

    /// <summary>Output of one mapper class; value-equatable so unchanged mappers are not regenerated.</summary>
    internal sealed class MapperResult : IEquatable<MapperResult>
    {
        public MapperResult(string hintName, string? source, ImmutableArray<Diagnostic> diagnostics, string? report = null, string mapperName = "",
            string? csv = null, string? json = null)
        {
            Csv = csv;
            Json = json;
            MapperName = mapperName;
            HintName = hintName;
            Source = source;
            Diagnostics = diagnostics;
            Report = report;
        }

        /// <summary>This mapper's section of the assembly's data-flow report (Markdown).</summary>
        public string? Report { get; }

        /// <summary>Fully qualified name of the mapper class (the key of its report section).</summary>
        public string MapperName { get; }

        /// <summary>This mapper's flows as CSV lines and as a JSON object.</summary>
        public string? Csv { get; }
        public string? Json { get; }

        public string HintName { get; }
        public string? Source { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public bool Equals(MapperResult? other) =>
            other != null && HintName == other.HintName && MapperName == other.MapperName && Source == other.Source && Csv == other.Csv && Json == other.Json && Report == other.Report && Diagnostics.SequenceEqual(other.Diagnostics);

        public override bool Equals(object? obj) => Equals(obj as MapperResult);

        public override int GetHashCode() => unchecked(HintName.GetHashCode() * 397 ^ (Source?.GetHashCode() ?? 0));
    }

    /// <summary>One mapper class's part of the data-flow report; value-equatable for incremental generation.</summary>
    internal sealed class ReportSection : IEquatable<ReportSection>
    {
        public ReportSection(string mapper, string markdown, string csv, string json)
        {
            Mapper = mapper;
            Markdown = markdown;
            Csv = csv;
            Json = json;
        }

        public string Mapper { get; }
        public string Markdown { get; }
        public string Csv { get; }
        public string Json { get; }

        public bool Equals(ReportSection? other) => other != null && Mapper == other.Mapper && Markdown == other.Markdown && Csv == other.Csv && Json == other.Json;

        public override bool Equals(object? obj) => Equals(obj as ReportSection);

        public override int GetHashCode() => unchecked(Mapper.GetHashCode() * 397 ^ Markdown.GetHashCode());
    }
}
