#if NET8_0_OR_GREATER
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// <summary>
    /// CM0029 and its fixes: hand-written copies and conversion operators turned into generated mappings.
    /// Each conversion is checked by running the code before and after the fix and comparing the results.
    /// </summary>
    public class MigrationTests
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static Project CreateProject(string? editorConfig, params (string Name, string Code)[] documents)
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
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), name, SourceText.From(code), filePath: "/app/" + name);
            if (editorConfig != null)
                solution = solution.AddAnalyzerConfigDocument(DocumentId.CreateNewId(projectId), ".editorconfig", SourceText.From(editorConfig), filePath: "/app/.editorconfig");
            return solution.GetProject(projectId)!;
        }

        private static Project CreateProject(params (string Name, string Code)[] documents) => CreateProject(null, documents);

        private static async Task<ImmutableArray<Diagnostic>> Analyze(Project project)
        {
            var compilation = (await project.GetCompilationAsync())!;
            var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HandWrittenCopyAnalyzer()), options)
                .GetAnalyzerDiagnosticsAsync();
            return diagnostics.Where(d => d.Id == "CM0029").ToImmutableArray();
        }

        private static async Task<CodeAction> Fix(Project project, Diagnostic diagnostic, string title)
        {
            var document = project.GetDocument(diagnostic.Location.SourceTree)!;
            var actions = new System.Collections.Generic.List<CodeAction>();
            await new ConvertCopyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic.Location.SourceSpan,
                ImmutableArray.Create(diagnostic), (a, _) => actions.Add(a), CancellationToken.None));
            return actions.Single(a => a.Title == title);
        }

        private static async Task<Project> Apply(CodeAction action)
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.Projects.Single();
        }

        private static async Task<string> Text(Project project, string name) =>
            (await project.Documents.Single(d => d.Name == name).GetTextAsync()).ToString().Replace("\r\n", "\n");

        /// <summary>Runs the generator, requires a clean compile, and returns what the static Probe.Run() returns.</summary>
        private static async Task<string> Run(Project project)
        {
            var compilation = (await project.GetCompilationAsync())!;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
            Assert.Empty(generatorDiagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
            using var stream = new MemoryStream();
            var emit = output.Emit(stream);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(stream.ToArray());
            var probe = assembly.GetTypes().Single(t => t.Name == "Probe");
            return (string)probe.GetMethod("Run")!.Invoke(null, null)!;
        }

        private const string Models = @"namespace Shop
{
    public enum Status { Draft, New }
    public class Address { public string City { get; set; } = """"; }
    public class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = """";
        public int Total { get; set; }
        public string Title { get; set; } = """";
        public Address Address { get; set; } = new Address();
        public string StrCity { get; set; } = """";
    }
}
";

        private const string OperatorDto = @"namespace Shop
{
    public class OrderDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = """";
        public long Total { get; set; }
        public string Label { get; set; } = """";
        public string City { get; set; } = """";
        public Status Status { get; set; }
        public string StrCity { get; set; } = ""unset"";
        public string Untouched { get; set; } = ""default"";

        public static implicit operator OrderDto(Order order) => new OrderDto
        {
            Id = order.Id,
            Name = order.Name,
            Total = order.Total,
            Label = order.Title,
            City = order.Address.City,
            Status = Status.New,
            StrCity = order.StrCity.Length > 3 ? order.StrCity.Substring(0, 3) : order.StrCity,
        };
    }
}
";

        private const string Probe = @"namespace Shop
{
    public static class Probe
    {
        private static string Describe(OrderDto d) => string.Join(""|"", d.Id, d.Name, d.Total, d.Label, d.City, d.Status, d.StrCity, d.Untouched);
        private static Order Sample() => new Order { Id = 7, Name = ""Pens"", Total = 12, Title = ""Office"", Address = new Address { City = ""Leeds"" }, StrCity = ""Sheffield"" };
        private static string Take(OrderDto d) => Describe(d);

        public static string Run()
        {
            OrderDto implicitly = Sample();
            var cast = (OrderDto)Sample();
            return Describe(implicitly) + "";"" + Describe(cast) + "";"" + Take(Sample());
        }
    }
}
";

        [Fact]
        public async Task Conversion_operator_is_reported_with_the_member_count()
        {
            var diagnostic = Assert.Single(await Analyze(CreateProject(("Models.cs", Models), ("OrderDto.cs", OperatorDto), ("Probe.cs", Probe))));
            Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
            Assert.Equal("The conversion operator to 'OrderDto' copies 5 members from 'order' by hand; it can be a generated mapping method", diagnostic.GetMessage());
            Assert.Equal("true", diagnostic.Properties["Operator"]);
            Assert.StartsWith("implicit operator OrderDto(Order order)", diagnostic.Location.SourceTree!.ToString().Substring(diagnostic.Location.SourceSpan.Start));
        }

        [Fact]
        public async Task Operator_body_becomes_a_generated_mapping_with_the_same_results()
        {
            var project = CreateProject(("Models.cs", Models), ("OrderDto.cs", OperatorDto), ("Probe.cs", Probe));
            var before = await Run(project);
            Assert.Equal("7|Pens|12|Office|Leeds|New|She|default;7|Pens|12|Office|Leeds|New|She|default;7|Pens|12|Office|Leeds|New|She|default", before);

            var diagnostic = Assert.Single(await Analyze(project));
            var fixedProject = await Apply(await Fix(project, diagnostic, "Convert to generated mapping"));
            var text = await Text(fixedProject, "OrderDto.cs");

            Assert.StartsWith("using ConventionMapper;\n", text);
            Assert.Contains("public static implicit operator OrderDto(Order order) => OrderDtoMappings.ToOrderDto(order, strCity: order.StrCity.Length > 3 ? order.StrCity.Substring(0, 3) : order.StrCity);", text);
            Assert.Contains(@"    [GeneratedMapper]
    public static partial class OrderDtoMappings
    {
        [MapExplicit(nameof(OrderDto.Id), nameof(OrderDto.Name), nameof(OrderDto.Total))]
        [MapMember(""Title"", nameof(OrderDto.Label))]
        [MapMember(""Address.City"", nameof(OrderDto.City))]
        [MapConstant(nameof(OrderDto.Status), Status.New)]
        public static partial OrderDto ToOrderDto(Order source, string strCity);
    }".Replace("\r\n", "\n"), text);

            Assert.Equal(before, await Run(fixedProject));
            Assert.Empty(await Analyze(fixedProject));
        }

        [Fact]
        public async Task Operator_can_be_removed_and_its_uses_rewritten()
        {
            var dto = OperatorDto.Replace(@"            StrCity = order.StrCity.Length > 3 ? order.StrCity.Substring(0, 3) : order.StrCity,
", "");
            const string elsewhere = "namespace Other\n{\n    public static class Elsewhere { public static Shop.OrderDto Get(Shop.Order o) => o; }\n}\n";
            var project = CreateProject(("Models.cs", Models), ("OrderDto.cs", dto), ("Probe.cs", Probe), ("Elsewhere.cs", elsewhere));
            var before = await Run(project);

            var diagnostic = Assert.Single(await Analyze(project));
            var fixedProject = await Apply(await Fix(project, diagnostic, "Replace the operator with a generated mapping (update call sites)"));
            Assert.Contains("Get(Shop.Order o) => global::Shop.OrderDtoMappings.ToOrderDto(o);", await Text(fixedProject, "Elsewhere.cs"));

            var model = await Text(fixedProject, "OrderDto.cs");
            Assert.DoesNotContain("operator", model);
            Assert.Contains("public static partial OrderDto ToOrderDto(Order source);", model);
            var probe = await Text(fixedProject, "Probe.cs");
            Assert.Contains("OrderDto implicitly = OrderDtoMappings.ToOrderDto(Sample());", probe);
            Assert.Contains("var cast = OrderDtoMappings.ToOrderDto(Sample());", probe);
            Assert.Contains("Take(OrderDtoMappings.ToOrderDto(Sample()))", probe);

            Assert.Equal(before, await Run(fixedProject));
        }

        [Fact]
        public async Task Replace_is_not_offered_when_the_operator_needs_parameters()
        {
            var project = CreateProject(("Models.cs", Models), ("OrderDto.cs", OperatorDto), ("Probe.cs", Probe));
            var diagnostic = Assert.Single(await Analyze(project));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Fix(project, diagnostic, "Replace the operator with a generated mapping (update call sites)"));
        }

        [Fact]
        public async Task Operator_used_by_a_foreach_conversion_is_kept_and_delegates()
        {
            var dto = OperatorDto.Replace(@"            StrCity = order.StrCity.Length > 3 ? order.StrCity.Substring(0, 3) : order.StrCity,
", "");
            var probe = Probe.Replace("return Describe(implicitly)", "var all = \"\"; foreach (OrderDto each in new[] { Sample() }) all += Describe(each);\n            return all + Describe(implicitly)");
            var project = CreateProject(("Models.cs", Models), ("OrderDto.cs", dto), ("Probe.cs", probe));
            var before = await Run(project);

            var fixedProject = await Apply(await Fix(project, Assert.Single(await Analyze(project)), "Replace the operator with a generated mapping (update call sites)"));

            Assert.Contains("public static implicit operator OrderDto(Order order) => OrderDtoMappings.ToOrderDto(order);", await Text(fixedProject, "OrderDto.cs"));
            Assert.Equal(before, await Run(fixedProject));
        }

        [Fact]
        public async Task Object_initializer_moves_into_the_files_mapper_class()
        {
            var code = @"using ConventionMapper;
namespace Shop
{
    public class Summary { public int Id { get; set; } public string Name { get; set; } = """"; public int Total { get; set; } public string Note { get; set; } = """"; }

    [GeneratedMapper]
    public static partial class ShopMappings
    {
    }

    public static class Probe
    {
        public static string Run()
        {
            var order = new Order { Id = 3, Name = ""Ink"", Total = 9 };
            var summary = new Summary { Id = order.Id, Name = order.Name, Total = order.Total, Note = ""for "" + order.Name };
            return string.Join(""|"", summary.Id, summary.Name, summary.Total, summary.Note);
        }
    }
}
";
            var project = CreateProject(("Models.cs", Models), ("Probe.cs", code));
            var before = await Run(project);
            Assert.Equal("3|Ink|9|for Ink", before);

            var diagnostic = Assert.Single(await Analyze(project));
            Assert.Equal("This object initializer copies 3 members from 'order' by hand; it can be a generated mapping method", diagnostic.GetMessage());
            var fixedProject = await Apply(await Fix(project, diagnostic, "Convert to generated mapping"));
            var text = await Text(fixedProject, "Probe.cs");

            Assert.Contains("var summary = ShopMappings.ToSummary(order, note: \"for \" + order.Name);", text);
            Assert.Contains(@"    public static partial class ShopMappings
    {
        [MapExplicit(nameof(Summary.Id), nameof(Summary.Name), nameof(Summary.Total))]
        public static partial Summary ToSummary(Order source, string note);
    }".Replace("\r\n", "\n"), text);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "using ConventionMapper;"));
            Assert.Equal(before, await Run(fixedProject));
        }

        [Fact]
        public async Task Small_nested_and_query_copies_are_not_reported()
        {
            var code = @"using System;
using System.Linq;
using System.Linq.Expressions;
namespace Shop
{
    public class Row { public int Id { get; set; } public string Name { get; set; } = """"; public int Total { get; set; } public Address Address { get; set; } = new Address(); }
    public static class Copies
    {
        public static Row Two(Order o) => new Row { Id = o.Id, Name = o.Name };
        public static Row Nested(Order o) => new Row { Id = o.Id, Name = o.Name, Total = o.Total, Address = { City = o.Address.City } };
        public static Expression<Func<Order, Row>> Projection => o => new Row { Id = o.Id, Name = o.Name, Total = o.Total };
        public static IQueryable<Row> Query(IQueryable<Order> orders) => from o in orders select new Row { Id = o.Id, Name = o.Name, Total = o.Total };
        public static Row Loose(Order o, Order p) => new Row { Id = o.Id, Name = p.Name, Total = 1 };
    }
}
";
            Assert.Empty(await Analyze(CreateProject(("Models.cs", Models), ("Copies.cs", code))));
        }

        [Fact]
        public async Task Threshold_comes_from_editorconfig()
        {
            var code = @"namespace Shop
{
    public class Row { public int Id { get; set; } public string Name { get; set; } = """"; }
    public static class Copies { public static Row Two(Order o) => new Row { Id = o.Id, Name = o.Name }; }
}
";
            Assert.Empty(await Analyze(CreateProject(("Models.cs", Models), ("Copies.cs", code))));
            var project = CreateProject("root = true\n[*.cs]\nconventionmapper.copy_min_members = 2\n", ("Models.cs", Models), ("Copies.cs", code));
            var diagnostic = Assert.Single(await Analyze(project));
            Assert.Equal("This object initializer copies 2 members from 'o' by hand; it can be a generated mapping method", diagnostic.GetMessage());
            Assert.Equal("new Row", diagnostic.Location.SourceTree!.ToString().Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        }
    }
}
#endif
