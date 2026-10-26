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
            // The mapper code and the report; the attribute declarations (with their doc comments) are left out.
            var source = string.Join("\n", driver.GetRunResult().GeneratedTrees
                .Where(t => !t.FilePath.EndsWith("ConventionMapper.Attributes.g.cs", StringComparison.Ordinal))
                .Select(t => t.ToString()));
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
    [MapConversion] private static string Describe(Address a) => a.City;
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
        public void Copying_a_sensitive_member_is_a_build_error_unless_the_destination_is_sensitive()
        {
            var result = Generate(@"
using ConventionMapper;
public class User { public int Id { get; set; } [Sensitive] public string PasswordHash { get; set; } = """"; }
public class UserDto { public int Id { get; set; } public string PasswordHash { get; set; } = """"; }
public class UserCache { public int Id { get; set; } [Sensitive] public string PasswordHash { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    public static partial UserDto ToDto(User u);
    public static partial UserCache ToCache(User u);
}");

            var leak = Assert.Single(result.Generator, d => d.Id == "CM0009");
            Assert.Equal(DiagnosticSeverity.Error, leak.Severity);
            Assert.Contains("UserDto.PasswordHash", leak.GetMessage());
            Assert.Empty(result.CompileErrors); // the generator-only project gets its own [Sensitive]
            // Copied once: into the protected UserCache, never into UserDto.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Source, @"PasswordHash = u\.PasswordHash").Cast<System.Text.RegularExpressions.Match>());
        }

        [Fact]
        public void Projections_are_expression_tree_safe()
        {
            var result = Generate("using System; using System.Linq; using System.Linq.Expressions;" + Models + @"
public class Dest { public int Id { get; set; } public string? AddressCity { get; set; } public int Score { get; set; } }
[GeneratedMapper] public static partial class M
{
    public static partial IQueryable<Dest> Project(this IQueryable<Source> q);
    public static partial Expression<Func<Source, Dest>> Projection();
}");

            Assert.DoesNotContain(result.Generator, d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Empty(result.CompileErrors); // ?., patterns or named arguments in an expression tree would be compile errors
            Assert.Contains("global::System.Linq.Queryable.Select(q, ", result.Source);
            Assert.Contains("x.Address.City", result.Source);
            Assert.Contains("x.Score ?? default(int)", result.Source);
        }

        [Fact]
        public void Projection_reports_unmapped_members()
        {
            var result = Generate("using System.Linq;" + Models + @"
public class Dest { public int Id { get; set; } public string Missing { get; set; } = """"; }
[GeneratedMapper] public static partial class M { public static partial IQueryable<Dest> Project(IQueryable<Source> q); }");

            Assert.Contains(result.Generator, d => d.Id == "CM0001" && d.GetMessage().Contains("Missing"));
            Assert.Empty(result.CompileErrors);
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

        [Fact]
        public void Unused_extra_parameter_is_an_error()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dest Map(Source s, int submissionNumber); }");

            Assert.Contains(result.Generator, d => d.Id == "CM0011" && d.GetMessage().Contains("submissionNumber"));
        }

        [Fact]
        public void MapMember_path_matching_a_source_member_and_a_parameter_is_an_error()
        {
            // Regression: "CreateUser" used to bind to the parameter "createUser" instead of the source member.
            var result = Generate(@"
using ConventionMapper;
public class Row { public int CreateUser { get; set; } }
public class Data { public int CreateUserId { get; set; } public string CreateUser { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    [MapExplicit]
    [MapMember(""CreateUser"", ""CreateUserId"")]
    public static partial Data Map(Row row, string createUser);
}");

            var error = Assert.Single(result.Generator, d => d.Id == "CM0014");
            Assert.Contains("'Row.CreateUser'", error.GetMessage());
            Assert.Contains("'createUser'", error.GetMessage());
            Assert.DoesNotContain("int.Parse(createUser)", result.Source);
        }

        [Fact]
        public void MapMember_path_through_a_parameter_still_works_when_unambiguous()
        {
            var result = Generate(@"
using ConventionMapper;
public class User { public string Name { get; set; } = """"; }
public class Row { public int CreateUser { get; set; } }
public class Data { public int CreateUserId { get; set; } public string CreateUserName { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    [MapMember(""CreateUser"", ""CreateUserId"")]
    [MapMember(""author.Name"", ""CreateUserName"")]
    public static partial Data Map(Row row, User author);
}");

            Assert.Empty(result.Generator.Where(d => d.Severity >= DiagnosticSeverity.Warning));
            Assert.Empty(result.CompileErrors);
            Assert.Contains("CreateUserId = row.CreateUser", result.Source);
            Assert.Contains("author.Name", result.Source);
        }

        // ------------------------------------------------------------ name collisions between source members and parameters

        private const string CollisionModels = @"
using ConventionMapper;
public class Customer { public string Name { get; set; } = """"; }
public class Policy { public int Count { get; set; } }
public class Row { public int CreateUser { get; set; } public string Status { get; set; } = """"; public Customer Customer { get; set; } = new Customer(); }
";

        [Fact]
        public void Dotted_MapMember_path_matching_a_source_member_and_a_parameter_is_an_error()
        {
            var result = Generate(CollisionModels + @"
public class Data { public string CustomerName { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    [MapMember(""Customer.Name"", ""CustomerName"")]
    public static partial Data Map(Row row, Customer customer);
}");

            Assert.Single(result.Generator, d => d.Id == "CM0014");
        }

        [Fact]
        public void Ambiguous_constructor_parameter_source_is_reported_once()
        {
            var result = Generate(CollisionModels + @"
public record Data(int CreateUserId);
[GeneratedMapper] public static partial class M
{
    [MapMember(""CreateUser"", ""CreateUserId"")]
    public static partial Data Map(Row row, string createUser);
}");

            Assert.Single(result.Generator, d => d.Id == "CM0014");
            Assert.DoesNotContain("int.Parse", result.Source);
        }

        [Fact]
        public void MapMember_from_the_source_wins_over_a_parameter_named_like_the_destination()
        {
            var result = Generate(CollisionModels + @"
public class Data { public string Status { get; set; } = """"; public int CreateUser { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapMember(""Customer.Name"", ""Status"")]
    public static partial Data Map(Row row, string status);
}");

            Assert.Contains("Status = row.Customer", result.Source);
            Assert.DoesNotContain("Status = status", result.Source);
            Assert.Contains(result.Generator, d => d.Id == "CM0011" && d.GetMessage().Contains("status"));
        }

        [Fact]
        public void A_parameter_named_like_the_destination_sets_it_even_when_the_source_has_that_member()
        {
            var result = Generate(CollisionModels + @"
public class Data { public string Status { get; set; } = """"; public int CreateUser { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Data Map(Row row, string status); }");

            Assert.Contains("Status = status", result.Source);
            Assert.Contains("CreateUser = row.CreateUser", result.Source);
            Assert.Empty(result.Generator.Where(d => d.Severity >= DiagnosticSeverity.Warning));
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Path_through_a_nullable_parameter_is_null_safe()
        {
            var result = Generate(CollisionModels + @"
public class Data { public int? Count { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapExplicit]
    [MapMember(""policy.Count"", ""Count"")]
    public static partial Data Map(Row row, Policy? policy);
}");

            Assert.Contains("policy?.Count", result.Source);
            Assert.Empty(result.Generator.Where(d => d.Severity >= DiagnosticSeverity.Warning));
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Explicit_mode_needs_no_ignores_and_checks_listed_names()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Unmatched { get; set; } = """"; }
[GeneratedMapper] public static partial class M { [MapExplicit(""Id"", ""Missing"")] public static partial Dest Map(Source s); }");

            Assert.DoesNotContain(result.Generator, d => d.Id == "CM0001");
            Assert.Contains(result.Generator, d => d.Id == "CM0008" && d.GetMessage().Contains("Missing"));
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Explicit_mode_reports_listed_members_without_a_source()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Unmatched { get; set; } = """"; }
[GeneratedMapper] public static partial class M { [MapExplicit(""Unmatched"")] public static partial Dest Map(Source s); }");

            Assert.Contains(result.Generator, d => d.Id == "CM0001" && d.GetMessage().Contains("Unmatched"));
        }

        [Fact]
        public void Null_substitute_problems_are_reported()
        {
            var result = Generate(Models + @"
public class Dest { public int Id { get; set; } public string Name { get; set; } = """"; public int Score { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapNullSubstitute(""Id"", 0)]
    [MapNullSubstitute(""Score"", ""zero"")]
    public static partial Dest Map(Source s);
}");

            Assert.Contains(result.Generator, d => d.Id == "CM0012" && d.GetMessage().Contains("'Id'"));
            Assert.Contains(result.Generator, d => d.Id == "CM0013" && d.GetMessage().Contains("'Score'"));
            Assert.Empty(result.CompileErrors);
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
        public async Task CreateMap_copying_a_sensitive_member_is_reported()
        {
            var diagnostics = await Analyze(@"
using ConventionMapper;
public class User { public int Id { get; set; } [Sensitive] public string PasswordHash { get; set; } = """"; }
public class UserDto { public int Id { get; set; } public string PasswordHash { get; set; } = """"; }
public class P : Profile { public P() { CreateMap<User, UserDto>(); } }");

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("CM1003", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
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
