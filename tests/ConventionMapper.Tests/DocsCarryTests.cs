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
using Microsoft.CodeAnalysis.Text;

namespace ConventionMapper.Tests
{
    /// <summary>Source member docs carried into the generated docs, write sets on hover, and the CM0033 fix.</summary>
    public class DocsCarryTests
    {
        private const string Code = @"using ConventionMapper;
namespace Shop
{
    public class Referral
    {
        public int Id { get; set; }
        /// <summary>Status of the referral: <see cref=""T:Shop.Referral""/> is <c>A</c> when active &amp; open.</summary>
        public string Status { get; set; } = """";
        /// <summary>
        /// Who sent it.
        /// </summary>
        public string Sender { get; set; } = """";
    }

    public class ReferralDto
    {
        public int Id { get; set; }
        public string Status { get; set; } = """";
        /// <summary>Already documented.</summary>
        public string Sender { get; set; } = """";
    }

    [GeneratedMapper]
    public static partial class ReferralMapper
    {
        /// <summary>Maps a referral.</summary>
        public static partial ReferralDto ToDto(Referral referral);
    }
}";

        [Fact]
        public void Generated_docs_carry_source_summaries_and_the_write_set()
        {
            var result = GeneratorHarness.Generate(Code, new Dictionary<string, string> { ["ConventionMapperGeneratedDocs"] = "true" });
            Assert.Empty(result.CompileErrors);
            Assert.Contains("<item><term>Status</term><description>Status: Status of the referral: Referral is A when active &amp; open.</description></item>", result.Source);
            Assert.Contains("<item><term>Sender</term><description>Sender: Who sent it.</description></item>", result.Source);
            Assert.Contains("<item><term>Id</term><description>Id</description></item>", result.Source); // no docs on the source
            Assert.Contains("/// Writes: Id, Sender, Status.", result.Source);
        }

        [Fact]
        public void Generated_code_is_embedded_only_when_asked()
        {
            var off = GeneratorHarness.Generate(Code);
            Assert.DoesNotContain("GeneratedCodeOf", off.Source);
            Assert.DoesNotContain("#cm-code", off.Source);

            var on = GeneratorHarness.Generate(Code, new Dictionary<string, string> { ["ConventionMapperEmbedGeneratedCode"] = "true" });
            Assert.Empty(on.CompileErrors);
            Assert.Contains("internal static string GeneratedCodeOf(string method, params global::System.Type[] parameterTypes)", on.Source);
            Assert.Contains("[\"ToDto(Referral)\"] = \"public static partial global::Shop.ReferralDto ToDto(global::Shop.Referral referral)\\n{", on.Source);
        }

        [Fact]
        public void Undocumented_destination_members_offer_the_source_summary()
        {
            var result = GeneratorHarness.Generate(Code);
            var hints = result.WithId("CM0033").ToList();
            var hint = Assert.Single(hints); // Sender is documented already, Id's source has no docs
            Assert.Equal(DiagnosticSeverity.Hidden, hint.Severity);
            Assert.Equal("'ReferralDto.Status' has no documentation; it is copied from 'Referral.Status', which has", hint.GetMessage());
        }

        [Fact]
        public async Task The_fix_copies_the_summary()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
                .WithProjectParseOptions(projectId, GeneratorHarness.ParseOptions)
                .AddMetadataReferences(projectId, references);
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(documentId, "Shop.cs", SourceText.From(Code));
            var project = solution.GetProject(projectId)!;

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: GeneratorHarness.ParseOptions);
            driver.RunGeneratorsAndUpdateCompilation((await project.GetCompilationAsync())!, out _, out var diagnostics);
            var hint = diagnostics.Single(d => d.Id == "CM0033");

            var actions = new List<CodeAction>();
            var document = project.GetDocument(documentId)!;
            await new GeneratorCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, hint.Location.SourceSpan, ImmutableArray.Create(hint), (a, _) => actions.Add(a), CancellationToken.None));
            var action = Assert.Single(actions);
            Assert.Equal("Copy documentation from 'Referral.Status'", action.Title);

            var changed = (await action.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single().ChangedSolution;
            var text = (await changed.GetDocument(documentId)!.GetTextAsync()).ToString();
            Assert.Contains(@"        public int Id { get; set; }
        /// <summary>
        /// Status of the referral: Referral is A when active &amp; open.
        /// </summary>
        /// <remarks>Copied from <c>Referral.Status</c>.</remarks>
        public string Status { get; set; } = """";
        /// <summary>Already documented.</summary>".Replace("\r\n", "\n"), text.Replace("\r\n", "\n"));
        }
    }
}
#endif
