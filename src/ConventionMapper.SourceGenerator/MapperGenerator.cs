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

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(static ctx =>
                ctx.AddSource("ConventionMapper.Attributes.g.cs", SourceText.From(AttributeSource, Encoding.UTF8)));

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
                if (result.Source != null) spc.AddSource(result.HintName, SourceText.From(ApplyDocs(result.Source, pair.Right), Encoding.UTF8));
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
                spc.AddSource("ConventionMapper.DataFlowReport.g.cs", SourceText.From(DataFlowReportSource.Build(parts, pair.Right), Encoding.UTF8));
            });
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
    /// Reuses the configuration ([MapMember], [MapIgnore], [MapExplicit], [MapNullSubstitute], [MapAllowParse]) of another
    /// mapping method of this class with the same source and destination types, for example an overload with extra parameters:
    /// <c>[MapInherit(nameof(ToDto))]</c>. Attributes on this method are added to (and win over) the inherited ones.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]
    [global::System.Diagnostics.Conditional(""CONVENTIONMAPPER_KEEP_ATTRIBUTES"")]
    internal sealed class MapInheritAttribute : global::System.Attribute
    {
        public MapInheritAttribute(string method)
        {
            Method = method;
        }

        public string Method { get; }
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
