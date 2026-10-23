#if NET8_0_OR_GREATER
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ConventionMapper.Analyzers;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.Tests
{
    /// <summary>Runs the source generator and the analyzer on in-memory compilations and checks their diagnostics.</summary>
    public class RoslynDiagnosticsTests
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static CSharpCompilation Compile(string code, bool referenceConventionMapper = false)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();
            if (referenceConventionMapper) references.Add(MetadataReference.CreateFromFile(typeof(MapperConfiguration).Assembly.Location));
            return CSharpCompilation.Create("Test" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(code, ParseOptions) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        }

        private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> CompileErrors, string Source) Generate(string code)
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(Compile(code), out var output, out var diagnostics);
            var source = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
            var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
            return (diagnostics, errors, source);
        }

        private const string Models = @"
using System.Collections.Generic;
using ConventionMapper;
public class Address { public string City { get; set; } = """"; }
public class Source { public int Id { get; set; } public string Name { get; set; } = """"; public Address? Address { get; set; } public int? Score { get; set; } }
";

        [Fact]
        public void Clean_mapper_generates_compilable_code_without_diagnostics()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Name { get; set; } = """"; public string? AddressCity { get; set; } public int? Score { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dest Map(Source s); }");

            Assert.Empty(result.Generator);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("s.Address?.City", result.Source);
        }

        [Fact]
        public void Unmapped_destination_member_is_an_error()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Missing { get; set; } = """"; }
[GeneratedMapper] public static partial class M { public static partial Dest Map(Source s); }");

            var diagnostic = Assert.Single(result.Generator, d => d.Id == "CM0001");
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Contains("Missing", diagnostic.GetMessage());
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void MapIgnore_and_MapMember_resolve_unmapped_members()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Missing { get; set; } = """"; public string Title { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Missing"")]
    [MapMember(""Name"", ""Title"")]
    public static partial Dest Map(Source s);
}");

            Assert.DoesNotContain(result.Generator, d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Static_conversion_methods_in_the_mapper_are_used()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Address { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    public static partial Dest Map(Source s);
    private static string Describe(Address a) => a.City;
}");

            Assert.DoesNotContain(result.Generator, d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Describe(s.Address)", result.Source);
        }

        [Fact]
        public void A_mapping_method_does_not_call_itself_for_its_own_root_conversion()
        {
            var result = Generate(Models + @"
public enum A { One, Two }
public enum B { One, Two }
[GeneratedMapper] public static partial class M { public static partial B Map(A value); }");

            Assert.Empty(result.CompileErrors);
            Assert.DoesNotContain("return Map(value);", result.Source);
        }

        [Fact]
        public void Mapper_must_be_partial()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } }
[GeneratedMapper] public static class M { }");

            Assert.Contains(result.Generator, d => d.Id == "CM0005");
        }

        [Fact]
        public void Incompatible_member_types_are_reported()
        {
            var result = Generate(Models + @"
public class Dest { public int Address { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dest Map(Source s); }");

            Assert.Contains(result.Generator, d => d.Id == "CM0002");
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Nullable_to_non_nullable_is_reported_as_info()
        {
            var result = Generate(Models + @"
public class Dest { public int Score { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dest Map(Source s); }");

            var diagnostic = Assert.Single(result.Generator, d => d.Id == "CM0003");
            Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        }

        [Fact]
        public void Unmatched_enum_values_and_unknown_attribute_members_are_warnings()
        {
            var result = Generate(Models + @"
public enum A { One, Two, Three }
public enum B { One, Two }
[GeneratedMapper] public static partial class M
{
    public static partial B Map(A value);
    [MapIgnore(""DoesNotExist"")]
    public static partial Address Copy(Address a);
}");

            Assert.Contains(result.Generator, d => d.Id == "CM0007" && d.GetMessage().Contains("Three"));
            Assert.Contains(result.Generator, d => d.Id == "CM0008" && d.GetMessage().Contains("DoesNotExist"));
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Unsupported_signature_is_reported()
        {
            var result = Generate(Models + @"
[GeneratedMapper] public static partial class M { public static partial void Map(Source s); }");

            Assert.Contains(result.Generator, d => d.Id == "CM0006");
        }

        // ------------------------------------------------------------ analyzer for the runtime API

        private static async Task<ImmutableArray<Diagnostic>> Analyze(string code)
        {
            var compilation = Compile(code, referenceConventionMapper: true);
            Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MappingAnalyzer())).GetAnalyzerDiagnosticsAsync();
        }

        private const string RuntimeModels = @"
using ConventionMapper;
public class Customer { public string Name { get; set; } = """"; }
public sealed class Order { public int Id { get; set; } public Customer Customer { get; set; } = new Customer(); }
public class OrderDto { public int Id { get; set; } public string CustomerName { get; set; } = """"; public string Note { get; set; } = """"; }
";

        [Fact]
        public async Task CreateMap_with_unmapped_member_is_reported()
        {
            var diagnostics = await Analyze(RuntimeModels + @"
public class P : Profile { public P() { CreateMap<Order, OrderDto>(); } }");

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("CM1001", diagnostic.Id);
            Assert.Contains("Note", diagnostic.GetMessage());
        }

        [Fact]
        public async Task CreateMap_with_further_configuration_is_not_checked()
        {
            var diagnostics = await Analyze(RuntimeModels + @"
public class P : Profile { public P() { CreateMap<Order, OrderDto>().ForMember(d => d.Note, o => o.Ignore()); } }");

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Untyped_map_with_known_source_type_is_suggested()
        {
            var diagnostics = await Analyze(RuntimeModels + @"
public static class Use
{
    public static OrderDto A(IMapper mapper, Order order) => mapper.Map<OrderDto>(order);
    public static OrderDto B(IMapper mapper, object order) => mapper.Map<OrderDto>(order);
}");

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("CM1002", diagnostic.Id);
        }
    }
}
#endif
