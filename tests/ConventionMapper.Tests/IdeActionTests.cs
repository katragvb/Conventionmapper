#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConventionMapper.Analyzers;
using ConventionMapper.CodeFixes;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.Tests
{
    /// <summary>Runs the IDE quick fixes and the "Generate build-time mapper" refactoring in a Roslyn workspace.</summary>
    public class IdeActionTests
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static Document CreateDocument(string code)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .Append(MetadataReference.CreateFromFile(typeof(MapperConfiguration).Assembly.Location));
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .WithProjectParseOptions(projectId, ParseOptions)
                .AddMetadataReferences(projectId, references)
                .AddDocument(documentId, "Profiles.cs", SourceText.From(code), new[] { "Mapping" });
            return solution.GetDocument(documentId)!;
        }

        private static async Task<Solution> Apply(CodeAction action)
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        }

        private const string Models = @"
using ConventionMapper;
namespace Shop
{
    public class Customer { public string Name { get; set; } = """"; }
    public sealed class Order { public int Id { get; set; } public Customer Customer { get; set; } = new Customer(); public string Secret { get; set; } = """"; public int Age { get; set; } }
    public class OrderDto { public int Id { get; set; } public string Buyer { get; set; } = """"; public string Secret { get; set; } = """"; public int Age { get; set; } public string Note { get; set; } = """"; }
";

        // ------------------------------------------------------------------ refactoring

        [Fact]
        public async Task Refactoring_writes_an_equivalent_generated_mapper()
        {
            var document = CreateDocument(Models + @"
    public class OrderProfile : Profile
    {
        public OrderProfile()
        {
            CreateMap<Order, OrderDto>()
                .ForMember(d => d.Secret, o => o.Ignore())
                .ForMember(d => d.Note, o => o.Ignore())
                .ForMember(d => d.Buyer, o => o.MapFrom(s => s.Customer.Name))
                .ForMember(d => d.Age, o => o.Condition(s => s.Age > 0));
        }
    }
}");
            var text = (await document.GetTextAsync()).ToString();
            var actions = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, new TextSpan(text.IndexOf("CreateMap<", StringComparison.Ordinal) + 2, 0), actions.Add, CancellationToken.None);
            await new ConvertToGeneratedMapperRefactoring().ComputeRefactoringsAsync(context);

            var action = Assert.Single(actions);
            Assert.Equal("Generate build-time mapper for Order → OrderDto", action.Title);

            var solution = await Apply(action);
            var mapperDocument = solution.Projects.Single().Documents.Single(d => d.Name == "OrderMapper.cs");
            Assert.Equal(new[] { "Mapping" }, mapperDocument.Folders);
            var mapper = (await mapperDocument.GetTextAsync()).ToString();
            Assert.Contains("namespace Shop", mapper);
            Assert.Contains("[GeneratedMapper]", mapper);
            Assert.Contains("public static partial class OrderMapper", mapper);
            Assert.Contains("[MapIgnore(\"Secret\")]", mapper);
            Assert.Contains("[MapMember(\"Customer.Name\", \"Buyer\")]", mapper);
            Assert.Contains("public static partial OrderDto ToOrderDto(this Order source);", mapper);
            Assert.Contains(".ForMember(d => d.Age, o => o.Condition(s => s.Age > 0))", mapper); // listed for manual conversion

            // The new file compiles with the source generator and maps everything it converted.
            var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public async Task Refactoring_adds_the_reverse_method_for_ReverseMap()
        {
            var document = CreateDocument(Models + @"
    public class P : Profile { public P() { CreateMap<Order, OrderDto>().ReverseMap(); } }
}");
            var text = (await document.GetTextAsync()).ToString();
            var actions = new List<CodeAction>();
            await new ConvertToGeneratedMapperRefactoring().ComputeRefactoringsAsync(
                new CodeRefactoringContext(document, new TextSpan(text.IndexOf("ReverseMap", StringComparison.Ordinal), 0), actions.Add, CancellationToken.None));

            var solution = await Apply(Assert.Single(actions));
            var mapper = (await solution.Projects.Single().Documents.Single(d => d.Name == "OrderMapper.cs").GetTextAsync()).ToString();
            Assert.Contains("public static partial Order ToOrder(this OrderDto source);", mapper);
        }

        // ------------------------------------------------------------------ quick fixes

        private static async Task<(Document Document, Diagnostic Diagnostic)> DiagnosticFor(string code, string id)
        {
            var document = CreateDocument(code);
            var compilation = (await document.Project.GetCompilationAsync())!;
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MappingAnalyzer())).GetAnalyzerDiagnosticsAsync();
            return (document, diagnostics.First(d => d.Id == id));
        }

        private static async Task<string> ApplyFix(Document document, Diagnostic diagnostic, int index = 0)
        {
            var actions = new List<CodeAction>();
            await new MappingCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None));
            var solution = await Apply(actions[index]);
            return (await solution.GetDocument(document.Id)!.GetTextAsync()).ToString();
        }

        [Fact]
        public async Task Unmapped_member_fix_adds_Ignore()
        {
            var (document, diagnostic) = await DiagnosticFor(Models + @"
    public class P : Profile { public P() { CreateMap<Order, OrderDto>(); } }
}", "CM1001");

            var fixedText = await ApplyFix(document, diagnostic);

            Assert.Contains(".ForMember(d => d.", fixedText);
            Assert.Contains("o => o.Ignore())", fixedText);
        }

        [Fact]
        public async Task Typed_map_fix_adds_the_source_type()
        {
            var (document, diagnostic) = await DiagnosticFor(Models + @"
    public static class Use { public static OrderDto Run(IMapper mapper, Order order) => mapper.Map<OrderDto>(order); }
}", "CM1002");

            var fixedText = await ApplyFix(document, diagnostic);

            Assert.Contains("mapper.Map<Order, OrderDto>(order)", fixedText);
        }
    }
}
#endif
