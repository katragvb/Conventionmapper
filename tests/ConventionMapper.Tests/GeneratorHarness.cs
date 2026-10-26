#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.Tests
{
    /// <summary>Runs the source generator on an in-memory compilation.</summary>
    internal static class GeneratorHarness
    {
        public static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        public sealed class Result
        {
            public ImmutableArray<Diagnostic> Generator { get; init; }
            public ImmutableArray<Diagnostic> CompileErrors { get; init; }
            public ImmutableArray<Diagnostic> CompileWarnings { get; init; }

            /// <summary>Generated mapper code (attribute declarations and the report excluded).</summary>
            public string Source { get; init; } = "";

            /// <summary>The generated DataFlowReport source.</summary>
            public string Report { get; init; } = "";
            public Compilation Output { get; init; } = null!;

            public IEnumerable<Diagnostic> WithId(string id) => Generator.Where(d => d.Id == id);
        }

        public static CSharpCompilation Compile(string code) =>
            CSharpCompilation.Create("Test" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(code, ParseOptions) },
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        public static Result Generate(string code, IDictionary<string, string>? buildProperties = null)
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new MapperGenerator().AsSourceGenerator() },
                parseOptions: ParseOptions,
                optionsProvider: new Options(buildProperties ?? new Dictionary<string, string>()));
            driver = driver.RunGeneratorsAndUpdateCompilation(Compile(code), out var output, out var diagnostics);
            var trees = driver.GetRunResult().GeneratedTrees;
            var compileDiagnostics = output.GetDiagnostics();
            return new Result
            {
                Generator = diagnostics,
                CompileErrors = compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray(),
                CompileWarnings = compileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToImmutableArray(),
                Source = string.Join("\n", trees.Where(t => !t.FilePath.EndsWith("ConventionMapper.Attributes.g.cs", StringComparison.Ordinal) &&
                                                            !t.FilePath.EndsWith("ConventionMapper.DataFlowReport.g.cs", StringComparison.Ordinal)).Select(t => t.ToString())),
                Report = string.Join("\n", trees.Where(t => t.FilePath.EndsWith("ConventionMapper.DataFlowReport.g.cs", StringComparison.Ordinal)).Select(t => t.ToString())),
                Output = output,
            };
        }

        private sealed class Options : AnalyzerConfigOptionsProvider
        {
            private readonly GlobalOptions _global;

            public Options(IDictionary<string, string> buildProperties)
            {
                _global = new GlobalOptions(buildProperties.ToDictionary(p => "build_property." + p.Key, p => p.Value));
            }

            public override AnalyzerConfigOptions GlobalOptions => _global;

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;
        }

        private sealed class GlobalOptions : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;

            public GlobalOptions(Dictionary<string, string> values)
            {
                _values = values;
            }

            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
        }
    }
}
#endif
