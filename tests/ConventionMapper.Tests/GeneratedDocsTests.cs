#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConventionMapper.Tests
{
    /// <summary>The generated files must compile without doc-comment warnings in projects that build XML docs (often with warnings as errors).</summary>
    public class GeneratedDocsTests
    {
        [Fact]
        public void Generated_files_have_valid_doc_comments()
        {
            var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose);
            var code = @"using ConventionMapper; using System.Collections.Generic; using System.Linq;
namespace Docs
{
    /// <summary>A source.</summary>
    public class Order { /// <summary>Id.</summary>
        public int Id { get; set; } }
    /// <summary>A destination.</summary>
    public class OrderDto { /// <summary>Id.</summary>
        public int Id { get; set; } }
    /// <summary>Maps.</summary>
    [GeneratedMapper]
    public static partial class OrderMapper
    {
        /// <summary>Maps one.</summary>
        public static partial OrderDto ToDto(Order order);
        /// <summary>Projects.</summary>
        public static partial IQueryable<OrderDto> Project(IQueryable<Order> query);
        /// <summary>Merges.</summary>
        [MapMergeKey(nameof(Order.Id))]
        public static partial void Merge(IEnumerable<Order> source, ICollection<OrderDto> target);
    }
}";
            var compilation = CSharpCompilation.Create("Docs", new[] { CSharpSyntaxTree.ParseText(code, parse) },
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: parse);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            var problems = output.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) == true)
                .ToList();
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }
    }
}
#endif
