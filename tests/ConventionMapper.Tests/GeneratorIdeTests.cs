#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConventionMapper.SourceGenerator;
using ConventionMapper.SourceGenerator.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.Tests
{
    /// <summary>The IDE fixes for the source generator's diagnostics (CM0001, CM0020, CM0023), in a Roslyn workspace.</summary>
    public class GeneratorIdeTests
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static Project CreateProject(params (string Name, string Code)[] documents)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .WithProjectParseOptions(projectId, ParseOptions)
                .AddMetadataReferences(projectId, references);
            foreach (var (name, code) in documents)
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), name, SourceText.From(code));
            return solution.GetProject(projectId)!;
        }

        /// <summary>Generator diagnostics (and the compilation with the generated code) for a project.</summary>
        private static async Task<(ImmutableArray<Diagnostic> Diagnostics, Compilation Output)> RunGenerator(Project project)
        {
            var compilation = (await project.GetCompilationAsync())!;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            return (diagnostics, output);
        }

        private static async Task<List<CodeAction>> Fixes(CodeFixProvider provider, Document document, params Diagnostic[] diagnostics)
        {
            var actions = new List<CodeAction>();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostics[0].Location.SourceSpan, diagnostics.ToImmutableArray(),
                (a, _) => actions.Add(a), CancellationToken.None));
            return actions;
        }

        private static async Task<Solution> Apply(CodeAction action)
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        }

        private static async Task<string> Text(Solution solution, string name) =>
            (await solution.Projects.Single().Documents.Single(d => d.Name == name).GetTextAsync()).ToString();

        private const string Models = @"using ConventionMapper;
namespace Shop
{
    public class Order { public int Id { get; set; } public string Name { get; set; } = """"; }
    public class OrderDto { public int Id { get; set; } public string Name { get; set; } = """"; public string Missing { get; set; } = """"; public string Other { get; set; } = """"; }
";

        // ------------------------------------------------------------------ CM0020 nameof

        [Fact]
        public async Task String_member_names_can_become_nameof()
        {
            var project = CreateProject(("Mapper.cs", Models + @"
    [GeneratedMapper]
    public static partial class OrderMapper
    {
        [MapExplicit(""Id"", ""name"")]
        [MapIgnore(""Missing"")]
        public static partial OrderDto ToDto(Order order);
    }
}"));
            var (diagnostics, _) = await RunGenerator(project);
            var nameofs = diagnostics.Where(d => d.Id == "CM0020").ToList();
            Assert.Equal(3, nameofs.Count);
            Assert.All(nameofs, d => Assert.Equal(DiagnosticSeverity.Hidden, d.Severity));

            var document = project.Documents.Single();
            var fixes = await Fixes(new GeneratorCodeFixProvider(), document, nameofs.First(d => d.GetMessage().Contains("\"name\"")));
            var action = Assert.Single(fixes);
            Assert.Equal("Use nameof(OrderDto.Name)", action.Title);

            var text = await Text(await Apply(action), "Mapper.cs");
            Assert.Contains("[MapExplicit(\"Id\", nameof(OrderDto.Name))]", text);
        }

        // ------------------------------------------------------------------ CM0001 fixes

        [Fact]
        public async Task Unmapped_member_offers_ignore_and_explicit_mode()
        {
            var project = CreateProject(("Mapper.cs", Models + @"
    [GeneratedMapper]
    public static partial class OrderMapper
    {
        /// <summary>Maps an order.</summary>
        public static partial OrderDto ToDto(Order order);
    }
}"));
            var (diagnostics, _) = await RunGenerator(project);
            var unmapped = diagnostics.Where(d => d.Id == "CM0001").OrderBy(d => d.GetMessage()).ToArray();
            Assert.Equal(2, unmapped.Length);

            var document = project.Documents.Single();
            var fixes = await Fixes(new GeneratorCodeFixProvider(), document, unmapped);
            Assert.Contains(fixes, f => f.Title == "Ignore 'Missing'");
            Assert.Contains(fixes, f => f.Title == "Map only the 2 mapped member(s) ([MapExplicit])");

            var ignoreAll = await Apply(fixes.Single(f => f.Title == "Ignore all 2 unmapped members"));
            var ignored = await Text(ignoreAll, "Mapper.cs");
            Assert.Contains("        /// <summary>Maps an order.</summary>\n        [MapIgnore(\"Missing\")]\n        [MapIgnore(\"Other\")]\n        public static partial OrderDto ToDto(Order order);", ignored.Replace("\r", ""));
            Assert.DoesNotContain((await RunGenerator(ignoreAll.Projects.Single())).Diagnostics, d => d.Id == "CM0001");

            var explicitMode = await Apply(fixes.First(f => f.Title.StartsWith("Map only", StringComparison.Ordinal)));
            Assert.Contains("[MapExplicit(\"Id\", \"Name\")]", await Text(explicitMode, "Mapper.cs"));
            Assert.DoesNotContain((await RunGenerator(explicitMode.Projects.Single())).Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        }

        [Fact]
        public async Task Listed_member_without_a_source_can_be_unlisted()
        {
            var project = CreateProject(("Mapper.cs", Models + @"
    [GeneratedMapper]
    public static partial class OrderMapper
    {
        [MapExplicit(""Id"", nameof(OrderDto.Missing))]
        public static partial OrderDto ToDto(Order order);
    }
}"));
            var (diagnostics, _) = await RunGenerator(project);
            var unmapped = diagnostics.Single(d => d.Id == "CM0001");

            var fixes = await Fixes(new GeneratorCodeFixProvider(), project.Documents.Single(), unmapped);
            var solution = await Apply(fixes.Single(f => f.Title == "Remove 'Missing' from [MapExplicit]"));

            Assert.Contains("[MapExplicit(\"Id\")]", await Text(solution, "Mapper.cs"));
            Assert.DoesNotContain((await RunGenerator(solution.Projects.Single())).Diagnostics, d => d.Id == "CM0001");
        }

        // ------------------------------------------------------------------ CM0023 assignments after a map

        private const string AssignModels = @"using ConventionMapper;
namespace Shop
{
    public class Order { public int Id { get; set; } }
    public class OrderDto { public int Id { get; set; } public string? Note { get; set; } public int Priority { get; set; } }

    [GeneratedMapper]
    public static partial class OrderMapper
    {
        [MapIgnore(""Note""), MapIgnore(""Priority"")]
        public static partial OrderDto ToDto(this Order order);
    }
}";

        private const string AssignUse = @"namespace Shop
{
    public static class Use
    {
        public static OrderDto Run(Order order, string note)
        {
            var dto = order.ToDto();
            dto.Note = note;
            dto.Priority = 5;
            return dto;
        }
    }
}";

        [Fact]
        public async Task Assignments_after_a_map_become_parameters_of_an_inheriting_overload()
        {
            var project = CreateProject(("Mapper.cs", AssignModels), ("Use.cs", AssignUse));
            var (_, output) = await RunGenerator(project);
            var analyzerDiagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AssignmentsAfterMapAnalyzer())).GetAnalyzerDiagnosticsAsync();
            var diagnostic = Assert.Single(analyzerDiagnostics);
            Assert.Equal("CM0023", diagnostic.Id);
            Assert.Contains("'Note', 'Priority' are assigned after 'ToDto'", diagnostic.GetMessage());

            var use = project.Documents.Single(d => d.Name == "Use.cs");
            var action = Assert.Single(await Fixes(new AssignmentsToParametersCodeFixProvider(), use, diagnostic));
            var solution = await Apply(action);

            var mapper = (await Text(solution, "Mapper.cs")).Replace("\r", "");
            Assert.Contains("[MapInherit(nameof(ToDto))]\n        public static partial OrderDto ToDto(this Order order, string? note, int priority);", mapper);
            var call = await Text(solution, "Use.cs");
            Assert.Contains("var dto = order.ToDto(note, 5);", call);
            Assert.DoesNotContain("dto.Note =", call);

            // The result builds with the generator, and the new overload sets both members.
            var (diagnostics, built) = await RunGenerator(solution.Projects.Single());
            Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Empty(built.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public async Task An_existing_overload_with_those_parameters_is_reused()
        {
            var mapper = AssignModels.Replace("public static partial OrderDto ToDto(this Order order);",
                "public static partial OrderDto ToDto(this Order order);\n        [MapInherit(nameof(ToDto))] public static partial OrderDto ToDto(this Order order, string? note, int priority);");
            var project = CreateProject(("Mapper.cs", mapper), ("Use.cs", AssignUse));
            var (_, output) = await RunGenerator(project);
            var diagnostic = (await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AssignmentsAfterMapAnalyzer())).GetAnalyzerDiagnosticsAsync()).Single();

            var solution = await Apply(Assert.Single(await Fixes(new AssignmentsToParametersCodeFixProvider(), project.Documents.Single(d => d.Name == "Use.cs"), diagnostic)));

            Assert.Equal(mapper, await Text(solution, "Mapper.cs"));
            Assert.Contains("order.ToDto(note, 5)", await Text(solution, "Use.cs"));
        }

        [Fact]
        public async Task Assignments_that_read_the_new_object_are_not_reported()
        {
            var project = CreateProject(("Mapper.cs", AssignModels), ("Use.cs", AssignUse.Replace("dto.Note = note;", "dto.Note = dto.Id.ToString();").Replace("dto.Priority = 5;", "")));
            var (_, output) = await RunGenerator(project);

            Assert.Empty(await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AssignmentsAfterMapAnalyzer())).GetAnalyzerDiagnosticsAsync());
        }
    }
}
#endif
