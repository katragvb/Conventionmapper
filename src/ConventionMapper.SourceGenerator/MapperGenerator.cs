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

            context.RegisterSourceOutput(mappers.Combine(docs), static (spc, pair) =>
            {
                var result = pair.Left;
                foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic);
                if (result.Source != null) spc.AddSource(result.HintName, Text(ApplyDocs(result.Source, pair.Right)));
            });

            // One data-flow report per assembly, with a section per mapper class.
            var reports = mappers
                .Select(static (result, _) => result.Report == null ? null : new ReportSection(result.MapperName, result.Report))
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
    /// <paramref name=""length""/> when given: <c>[MapTruncate(""City"")]</c>.
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
        public MapperResult(string hintName, string? source, ImmutableArray<Diagnostic> diagnostics, string? report = null, string mapperName = "")
        {
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

        public string HintName { get; }
        public string? Source { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public bool Equals(MapperResult? other) =>
            other != null && HintName == other.HintName && MapperName == other.MapperName && Source == other.Source && Report == other.Report && Diagnostics.SequenceEqual(other.Diagnostics);

        public override bool Equals(object? obj) => Equals(obj as MapperResult);

        public override int GetHashCode() => unchecked(HintName.GetHashCode() * 397 ^ (Source?.GetHashCode() ?? 0));
    }

    /// <summary>One mapper class's part of the data-flow report; value-equatable for incremental generation.</summary>
    internal sealed class ReportSection : IEquatable<ReportSection>
    {
        public ReportSection(string mapper, string markdown)
        {
            Mapper = mapper;
            Markdown = markdown;
        }

        public string Mapper { get; }
        public string Markdown { get; }

        public bool Equals(ReportSection? other) => other != null && Mapper == other.Mapper && Markdown == other.Markdown;

        public override bool Equals(object? obj) => Equals(obj as ReportSection);

        public override int GetHashCode() => unchecked(Mapper.GetHashCode() * 397 ^ Markdown.GetHashCode());
    }
}
