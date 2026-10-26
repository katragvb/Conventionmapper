#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using static ConventionMapper.Tests.GeneratorHarness;

namespace ConventionMapper.Tests
{
    /// <summary>1.8.0: ambiguous bindings, risky conversions, the conversion-method trap, [MapInherit], projections, docs.</summary>
    public class GeneratorSafetyTests
    {
        private const string Usings = "using System; using System.Collections.Generic; using System.Linq; using System.Linq.Expressions; using ConventionMapper;\n";

        private static void AssertClean(Result result)
        {
            Assert.DoesNotContain(result.Generator, d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Empty(result.CompileErrors);
        }

        // ------------------------------------------------------------------ CM0015 ambiguous sources

        [Fact]
        public void Property_and_Get_method_with_the_same_name_are_ambiguous()
        {
            var result = Generate(Usings + @"
public class Order { public decimal Total { get; set; } public decimal GetTotal() => 0m; }
public class OrderDto { public decimal Total { get; set; } }
[GeneratedMapper] public static partial class M { public static partial OrderDto Map(Order o); }");

            var error = Assert.Single(result.WithId("CM0015"));
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("'Order.Total'", error.GetMessage());
            Assert.Contains("'Order.GetTotal()'", error.GetMessage());
            Assert.DoesNotContain("Total = ", result.Source);
            Assert.Empty(result.WithId("CM0001"));
        }

        [Fact]
        public void Members_differing_only_in_case_are_ambiguous()
        {
            var result = Generate(Usings + @"
public class Row { public int Id { get; set; } public int ID; }
public class Dto { public int Id { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dto Map(Row r); }");

            Assert.Single(result.WithId("CM0015"));
        }

        [Fact]
        public void Two_flattenings_are_ambiguous()
        {
            var result = Generate(Usings + @"
public class Address { public string City { get; set; } = """"; }
public class Billing { public string AddressCity { get; set; } = """"; }
public class Customer { public Billing? Billing { get; set; } public Address? BillingAddress { get; set; } }
public class Dto { public string? BillingAddressCity { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dto Map(Customer c); }");

            var error = Assert.Single(result.WithId("CM0015"));
            Assert.Contains("'Customer.Billing.AddressCity'", error.GetMessage());
            Assert.Contains("'Customer.BillingAddress.City'", error.GetMessage());
        }

        [Fact]
        public void MapMember_resolves_an_ambiguity()
        {
            var result = Generate(Usings + @"
public class Order { public decimal Total { get; set; } public decimal GetTotal() => 0m; }
public class OrderDto { public decimal Total { get; set; } }
[GeneratedMapper] public static partial class M { [MapMember(""GetTotal"", ""Total"")] public static partial OrderDto Map(Order o); }");

            AssertClean(result);
            Assert.Contains("Total = o.GetTotal()", result.Source);
        }

        [Fact]
        public void Explicit_path_prefers_the_exact_case()
        {
            var result = Generate(Usings + @"
public class Row { public int Id { get; set; } public int ID; }
public class Dto { public int Key { get; set; } }
[GeneratedMapper] public static partial class M { [MapMember(""ID"", ""Key"")] public static partial Dto Map(Row r); }");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("Key = r.ID", result.Source);
        }

        [Fact]
        public void Parameter_versus_a_flattened_path_is_ambiguous()
        {
            var result = Generate(Usings + @"
public class Customer { public string Name { get; set; } = """"; }
public class Order { public Customer Customer { get; set; } = new Customer(); }
public class Dto { public string CustomerName { get; set; } = """"; }
[GeneratedMapper] public static partial class M { public static partial Dto Map(Order o, string customerName); }");

            var error = Assert.Single(result.WithId("CM0015"));
            Assert.Contains("the parameter 'customerName'", error.GetMessage());
            Assert.Contains("'Order.Customer.Name'", error.GetMessage());
        }

        [Fact]
        public void Ambiguous_constructor_parameter_is_reported()
        {
            var result = Generate(Usings + @"
public class Order { public decimal Total { get; set; } public decimal GetTotal() => 0m; }
public record OrderDto(decimal Total);
[GeneratedMapper] public static partial class M { public static partial OrderDto Map(Order o); }");

            Assert.Single(result.WithId("CM0015"));
        }

        // ------------------------------------------------------------------ CM0016 parsing and narrowing

        [Fact]
        public void Parsing_and_narrowing_conversions_are_warnings()
        {
            var result = Generate(Usings + @"
public enum Kind { A, B }
public class Row { public string Code { get; set; } = """"; public long Count { get; set; } public string Kind { get; set; } = """"; }
public class Dto { public int Code { get; set; } public int Count { get; set; } public Kind Kind { get; set; } }
[GeneratedMapper] public static partial class M { public static partial Dto Map(Row r); }");

            var warnings = result.WithId("CM0016").ToList();
            Assert.Equal(3, warnings.Count);
            Assert.All(warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
            Assert.Contains(warnings, w => w.GetMessage().Contains("int.Parse") && w.GetMessage().Contains("'Dto.Code'"));
            Assert.Contains(warnings, w => w.GetMessage().Contains("narrowing cast (long to int)"));
            Assert.Contains(warnings, w => w.GetMessage().Contains("Enum.Parse"));
            Assert.Empty(result.CompileErrors);
            Assert.Contains("⚠ int.Parse", result.Report);
            Assert.Contains("Review first:", result.Report);
        }

        [Fact]
        public void MapAllowParse_on_the_method_or_class_allows_them()
        {
            var result = Generate(Usings + @"
public class Row { public string Code { get; set; } = """"; public long Count { get; set; } }
public class Dto { public int Code { get; set; } public int Count { get; set; } }
[GeneratedMapper, MapAllowParse(""Count"")] public static partial class M
{
    [MapAllowParse(""Code"")] public static partial Dto Map(Row r);
}");

            AssertClean(result);
            Assert.Contains("parsed (allowed)", result.Report);
        }

        [Fact]
        public void Unknown_MapAllowParse_member_is_reported()
        {
            var result = Generate(Usings + @"
public class Row { public int Code { get; set; } }
public class Dto { public int Code { get; set; } }
[GeneratedMapper] public static partial class M { [MapAllowParse(""Missing"")] public static partial Dto Map(Row r); }");

            Assert.Contains(result.WithId("CM0008"), d => d.GetMessage().Contains("Missing"));
        }

        [Fact]
        public void Parsing_inside_a_collection_is_reported_for_every_member_that_uses_it()
        {
            var result = Generate(Usings + @"
public class Row { public List<string> A { get; set; } = new(); public List<string> B { get; set; } = new(); }
public class Dto { public List<int> A { get; set; } = new(); public List<int> B { get; set; } = new(); }
[GeneratedMapper] public static partial class M { public static partial Dto Map(Row r); }");

            Assert.Equal(2, result.WithId("CM0016").Count());
        }

        [Fact]
        public void A_root_conversion_method_is_not_a_warning()
        {
            var result = Generate(Usings + @"[GeneratedMapper] public static partial class M { public static partial int Map(string s); }");

            Assert.Empty(result.WithId("CM0016"));
            Assert.Empty(result.CompileErrors);
        }

        // ------------------------------------------------------------------ CM0017 / CM0018 conversion methods

        private const string MoneyModels = @"
public class Money { public decimal Amount { get; set; } }
public class Product { public Money Price { get; set; } = new(); public Money Cost { get; set; } = new(); }
public class ProductDto { public string Price { get; set; } = """"; public string Cost { get; set; } = """"; }
";

        [Fact]
        public void Unmarked_static_method_used_as_a_conversion_is_a_warning_at_the_method()
        {
            var result = Generate(Usings + MoneyModels + @"
[GeneratedMapper] public static partial class M
{
    public static partial ProductDto Map(Product p);
    private static string Format(Money m) => m.Amount.ToString();
}");

            var warning = Assert.Single(result.WithId("CM0017"));
            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            Assert.Contains("'Format'", warning.GetMessage());
            Assert.Contains("'ProductDto.Cost', 'ProductDto.Price'", warning.GetMessage());
            Assert.Equal("Format", warning.Location.SourceTree!.GetText().ToString(warning.Location.SourceSpan));
            Assert.Contains("| `Format()` | Money → string | `ProductDto.Cost`, `ProductDto.Price` |", result.Report);
            Assert.Contains("via `Format()`", result.Report);
        }

        [Fact]
        public void MapConversion_marks_an_intended_conversion()
        {
            var result = Generate(Usings + MoneyModels + @"
[GeneratedMapper] public static partial class M
{
    public static partial ProductDto Map(Product p);
    [MapConversion] private static string Format(Money m) => m.Amount.ToString();
}");

            AssertClean(result);
            Assert.Contains("Price = Format(p.Price)", result.Source);
            Assert.Contains("| `Format()` ([MapConversion]) |", result.Report);
        }

        [Fact]
        public void RequireMapConversion_ignores_unmarked_methods()
        {
            var result = Generate(Usings + MoneyModels + @"
[GeneratedMapper(RequireMapConversion = true)] public static partial class M
{
    public static partial ProductDto Map(Product p);
    private static string Format(Money m) => m.Amount.ToString();
}");

            Assert.Empty(result.WithId("CM0017"));
            Assert.DoesNotContain("Format(", result.Source);
            Assert.Contains(result.WithId("CM0002"), d => d.GetMessage().Contains("Price"));
        }

        [Fact]
        public void MapConversion_on_a_method_that_cannot_convert_is_an_error()
        {
            var result = Generate(Usings + MoneyModels + @"
[GeneratedMapper] public partial class M
{
    [MapConversion] private string Format(Money m) => m.Amount.ToString();
    [MapConversion] private static string Join(Money a, Money b) => """";
}");

            Assert.Equal(2, result.WithId("CM0018").Count());
        }

        // ------------------------------------------------------------------ CM0019 [MapInherit]

        private const string LocationModels = @"
public class LocationData { public int LocationNumber { get; set; } public string Street1 { get; set; } = """"; public string City { get; set; } = """"; public string Internal { get; set; } = """"; }
public class ImportData { public int LocationNumber { get; set; } public string Street1 { get; set; } = """"; public string Town { get; set; } = """"; public string? GlClassCode { get; set; } public decimal? Exposure { get; set; } public string Internal { get; set; } = """"; }
";

        [Fact]
        public void MapInherit_reuses_another_methods_configuration()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapExplicit(""LocationNumber"", ""Street1"")]
    [MapMember(""City"", ""Town"")]
    public static partial ImportData ToImportData(this LocationData source);

    [MapInherit(nameof(ToImportData))]
    public static partial ImportData ToImportData(this LocationData source, string? glClassCode, decimal? exposure);
}");

            AssertClean(result);
            var overload = result.Source.Substring(result.Source.IndexOf("ToImportData(this global::LocationData source, string? glClassCode", System.StringComparison.Ordinal));
            Assert.Contains("Town = source.City", overload);
            Assert.Contains("GlClassCode = glClassCode", overload);
            Assert.Contains("Exposure = exposure", overload);
            Assert.DoesNotContain("Internal =", result.Source);
        }

        [Fact]
        public void Own_attributes_win_over_inherited_ones()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapMember(""City"", ""Town"")]
    public static partial ImportData ToImportData(LocationData source);

    [MapInherit(nameof(ToImportData))]
    [MapMember(""Street1"", ""Town"")]
    [MapIgnore(""Internal"")]
    public static partial ImportData ToImportDataWithStreet(LocationData source, string? glClassCode, decimal? exposure);
}");

            Assert.Empty(result.WithId("CM0019"));
            var second = result.Source.Substring(result.Source.IndexOf("ToImportDataWithStreet(", System.StringComparison.Ordinal));
            Assert.Contains("Town = source.Street1", second);
        }

        [Fact]
        public void A_parameter_overrides_an_inherited_ignore_but_not_the_methods_own()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""GlClassCode""), MapIgnore(""Exposure""), MapIgnore(""Town""), MapIgnore(""Internal"")]
    public static partial ImportData ToImportData(LocationData source);

    [MapInherit(nameof(ToImportData))]
    public static partial ImportData ToImportData(LocationData source, string? glClassCode, decimal? exposure);

    [MapInherit(nameof(ToImportData))]
    [MapIgnore(""GlClassCode"")]
    public static partial ImportData WithOwnIgnore(LocationData source, string? glClassCode);
}");

            var overload = result.Source.Substring(result.Source.IndexOf("string? glClassCode, decimal? exposure)", System.StringComparison.Ordinal));
            Assert.Contains("GlClassCode = glClassCode", overload);
            Assert.Contains("Exposure = exposure", overload);
            Assert.DoesNotContain("Town =", overload.Substring(0, overload.IndexOf("WithOwnIgnore", System.StringComparison.Ordinal)));
            Assert.Single(result.WithId("CM0011"), d => d.GetMessage().Contains("'WithOwnIgnore'"));
        }

        [Fact]
        public void MapInherit_with_other_types_is_an_error()
        {
            var result = Generate(Usings + LocationModels + @"
public class Other { public int LocationNumber { get; set; } }
[GeneratedMapper] public static partial class M
{
    public static partial Other ToOther(LocationData source);

    [MapInherit(nameof(ToOther))]
    [MapIgnore(""GlClassCode""), MapIgnore(""Exposure""), MapIgnore(""Town"")]
    public static partial ImportData ToImportData(LocationData source);
}");

            var error = Assert.Single(result.WithId("CM0019"));
            Assert.Contains("'ToOther' maps LocationData to Other", error.GetMessage());
        }

        [Fact]
        public void MapInherit_of_an_unknown_method_or_a_cycle_is_an_error()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapInherit(""Nope"")]
    [MapExplicit]
    public static partial ImportData A(LocationData source);

    [MapInherit(nameof(C)), MapExplicit] public static partial ImportData B(LocationData source);
    [MapInherit(nameof(B)), MapExplicit] public static partial ImportData C(LocationData source);
}");

            var errors = result.WithId("CM0019").Select(d => d.GetMessage()).ToList();
            Assert.Contains(errors, m => m.Contains("no other method named 'Nope'"));
            Assert.Contains(errors, m => m.Contains("cycle"));
        }

        [Fact]
        public void A_projection_can_inherit_from_a_create_method()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapExplicit(""LocationNumber"")]
    [MapMember(""City"", ""Town"")]
    public static partial ImportData ToImportData(LocationData source);

    [MapInherit(nameof(ToImportData))]
    public static partial IQueryable<ImportData> Project(IQueryable<LocationData> query);
}");

            AssertClean(result);
            Assert.Contains("Town = x.City", result.Source);
            Assert.DoesNotContain("Street1 = x.Street1", result.Source);
        }

        // ------------------------------------------------------------------ report structure

        [Fact]
        public void Report_groups_overloads_and_flags_key_writes()
        {
            var result = Generate(Usings + @"
public class Line { public int Id { get; set; } public string Sku { get; set; } = """"; }
public class LineDto { public int Id { get; set; } public string Sku { get; set; } = """"; }
public class Order { public int CustomerId { get; set; } public Line Line { get; set; } = new(); }
public class OrderDto { public int CustomerId { get; set; } public LineDto Line { get; set; } = new(); public string? Note { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Note"")] public static partial OrderDto ToDto(Order o);
    public static partial OrderDto ToDto(Order o, string? note);
}");

            AssertClean(result);
            var report = result.Report;
            var single = report.Substring(report.IndexOf("ByMapper =", System.StringComparison.Ordinal));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(single, "### ToDto\n"));
            Assert.Contains("#### `ToDto(Order)` → `OrderDto`", report);
            Assert.Contains("#### `ToDto(Order, string?)` → `OrderDto`", report);
            Assert.Contains("### Nested maps", report);
            Assert.Contains("#### `Line` → `LineDto`", report);
            Assert.DoesNotContain("Review first", report); // DTO ids are not keys
            Assert.Contains("[\"M\"] = @\"# Data-flow report", report);
            // Method sections come in declaration order: the overload without a note first.
            Assert.True(report.IndexOf("`ToDto(Order)`", System.StringComparison.Ordinal) < report.IndexOf("`ToDto(Order, string?)`", System.StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ keys in "Review first"

        [Fact]
        public void Names_alone_do_not_make_keys()
        {
            var result = Generate(Usings + @"
public class Row { public int Id { get; set; } public int ErrorObjectId { get; set; } public int RecordID { get; set; } public int ReadByUserId { get; set; } }
public class RowDto { public int Id { get; set; } public int ErrorObjectId { get; set; } public int RecordID { get; set; } public int ReadByUserId { get; set; } }
[GeneratedMapper] public static partial class M { public static partial RowDto Map(Row r); }");

            AssertClean(result);
            Assert.DoesNotContain("Review first", result.Report);
            Assert.DoesNotContain("key", result.Report.Substring(result.Report.IndexOf("ByMapper =", System.StringComparison.Ordinal)));
        }

        [Fact]
        public void Key_attributes_mark_keys()
        {
            var result = Generate(Usings + @"
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
public class Row { public int Code { get; set; } public int OwnerRef { get; set; } public int Tenant { get; set; } public int Plain { get; set; } }
[Microsoft.EntityFrameworkCore.PrimaryKey(nameof(Tenant), nameof(Code))]
public class Entity
{
    [Key] public int Code { get; set; }
    [ForeignKey(""Owner"")] public int OwnerRef { get; set; }
    public int Tenant { get; set; }
    public int Plain { get; set; }
}
[GeneratedMapper] public static partial class M { public static partial Entity Map(Row r); }");

            AssertClean(result);
            Assert.Contains("`Entity.Code` (key)", result.Report);
            Assert.Contains("`Entity.OwnerRef` (key)", result.Report);
            Assert.Contains("`Entity.Tenant` (key)", result.Report);
            Assert.DoesNotContain("`Entity.Plain` (key)", result.Report);
        }

        private const string EntityModels = Usings + @"
using Microsoft.EntityFrameworkCore;
public class Customer { public int Id { get; set; } public string Name { get; set; } = """"; }
public class Policy
{
    public int PolicyId { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ErrorObjectId { get; set; }
    public int ReadByUserId { get; set; }
}
public class Shop : DbContext { public DbSet<Policy> Policies => Set<Policy>(); }
public class PolicyRow { public int PolicyId { get; set; } public int CustomerId { get; set; } public int ErrorObjectId { get; set; } public int ReadByUserId { get; set; } }
";

        [Fact]
        public void Entity_keys_follow_EF_Core_conventions()
        {
            var result = Generate(EntityModels + @"
[GeneratedMapper] public static partial class M { [MapIgnore(""Customer"")] public static partial Policy ToEntity(PolicyRow r); }");

            AssertClean(result);
            Assert.Contains("`Policy.PolicyId` (key)", result.Report);   // <Type>Id: primary key
            Assert.Contains("`Policy.CustomerId` (key)", result.Report); // <Navigation>Id: foreign key
            Assert.DoesNotContain("`Policy.ErrorObjectId` (key)", result.Report); // no ErrorObject navigation
            Assert.DoesNotContain("`Policy.ReadByUserId` (key)", result.Report);
        }

        [Fact]
        public void MapReviewed_takes_keys_off_the_review_list()
        {
            var result = Generate(EntityModels + @"
[GeneratedMapper, MapReviewed(nameof(Policy.PolicyId))] public static partial class M
{
    [MapIgnore(""Customer""), MapReviewed(nameof(Policy.CustomerId))]
    public static partial Policy ToEntity(PolicyRow r);

    [MapInherit(nameof(ToEntity))]
    public static partial Policy ToEntity(PolicyRow r, int errorObjectId);

    [MapReviewed(""Nope""), MapIgnore(""Customer"")]
    public static partial Policy Other(PolicyRow r);
}");

            Assert.DoesNotContain("`Policy.PolicyId` (key)", result.Report);
            Assert.Contains("| PolicyId | `PolicyId` | key (reviewed) |", result.Report);
            var first = result.Report.Substring(result.Report.IndexOf("#### `ToEntity(PolicyRow)`", System.StringComparison.Ordinal));
            Assert.Contains("| CustomerId | `CustomerId` | key (reviewed) |", first.Substring(0, first.IndexOf("####", 5, System.StringComparison.Ordinal)));
            var inherited = result.Report.Substring(result.Report.IndexOf("#### `ToEntity(PolicyRow, int)`", System.StringComparison.Ordinal));
            Assert.Contains("| CustomerId | `CustomerId` | key (reviewed) |", inherited.Substring(0, inherited.IndexOf("####", 5, System.StringComparison.Ordinal)));
            Assert.Contains("`Policy.CustomerId` (key) is written", result.Report); // still listed for Other
            Assert.Contains(result.WithId("CM0008"), d => d.GetMessage().Contains("'Nope' in [MapReviewed]"));
        }

        // ------------------------------------------------------------------ [MapInherit] overload choice

        [Fact]
        public void MapInherit_with_several_overloads_needs_parameter_types()
        {
            var result = Generate(Usings + LocationModels + @"
[GeneratedMapper] public static partial class M
{
    [MapExplicit(""LocationNumber"")] public static partial ImportData ToImportData(LocationData source);
    [MapExplicit(""Street1"")] public static partial ImportData ToImportData(LocationData source, string? glClassCode);

    [MapInherit(nameof(ToImportData))]
    public static partial ImportData Ambiguous(LocationData source, string? glClassCode, decimal? exposure);

    [MapInherit(nameof(ToImportData), typeof(LocationData), typeof(string))]
    public static partial ImportData Chosen(LocationData source, string? glClassCode, decimal? exposure);

    [MapInherit(nameof(ToImportData), typeof(int))]
    public static partial ImportData Missing(LocationData source, string? glClassCode, decimal? exposure);
}");

            var errors = result.WithId("CM0019").Select(d => d.GetMessage()).ToList();
            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, m => m.Contains("'Ambiguous'") && m.Contains("matches 2 overloads") && m.Contains("[MapInherit(nameof(ToImportData), typeof(LocationData))]"));
            Assert.Contains(errors, m => m.Contains("'Missing'") && m.Contains("no overload has the parameters (int)"));
            var chosen = result.Source.Substring(result.Source.IndexOf("Chosen(", System.StringComparison.Ordinal));
            Assert.Contains("Street1 = source.Street1", chosen.Substring(0, chosen.IndexOf("Missing(", System.StringComparison.Ordinal)));
        }

        // ------------------------------------------------------------------ typed write sets, line endings

        [Fact]
        public void WriteSetOf_finds_a_method_by_name_and_parameter_types()
        {
            var result = Generate(Usings + @"
public class Order { public int Id { get; set; } public string Name { get; set; } = """"; }
public class OrderDto { public int Id { get; set; } public string Name { get; set; } = """"; public string? Note { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Note"")] public static partial OrderDto ToDto(Order o);
    public static partial OrderDto ToDto(Order o, string? note);
}");

            AssertClean(result);
            Assert.Contains("internal static global::System.Collections.Generic.IReadOnlyList<string> WriteSetOf(string method, params global::System.Type[] parameterTypes)", result.Source);
            Assert.Contains("parameterTypes.Length == 2 && parameterTypes[0] == typeof(global::Order) && parameterTypes[1] == typeof(string)", result.Source);
        }

        [Fact]
        public void An_old_generator_still_loaded_after_an_upgrade_is_reported()
        {
            const string code = Usings + @"
public class Order { public int Id { get; set; } }
public class OrderDto { public int Id { get; set; } }
[GeneratedMapper] public static partial class M { public static partial OrderDto ToDto(Order o); }";
            var running = ConventionMapper.SourceGenerator.MapperGenerator.RunningVersion();
            Assert.Matches(@"^\d+\.\d+\.\d+", running);

            var stale = Generate(code, new Dictionary<string, string> { ["ConventionMapperSourceGeneratorVersion"] = "99.0.0" });
            var warning = Assert.Single(stale.WithId("CM0024"));
            Assert.Contains(running + " is running, but the project references 99.0.0", warning.GetMessage());

            Assert.Empty(Generate(code, new Dictionary<string, string> { ["ConventionMapperSourceGeneratorVersion"] = running }).WithId("CM0024"));
            Assert.Empty(Generate(code, new Dictionary<string, string> { ["ConventionMapperSourceGeneratorVersion"] = "__CONVENTIONMAPPER_VERSION__" }).WithId("CM0024"));
            Assert.Empty(Generate(code).WithId("CM0024"));
        }

        [Fact]
        public void Generated_files_use_only_LF_line_endings()
        {
            var result = Generate(Usings + @"
public class Order { public int Id { get; set; } }
public class OrderDto { public int Id { get; set; } }
[GeneratedMapper] public static partial class M { public static partial OrderDto ToDto(Order o); }");

            foreach (var tree in result.Output.SyntaxTrees.Where(t => t.FilePath.Contains("ConventionMapper", System.StringComparison.Ordinal)))
                Assert.DoesNotContain("\r", tree.ToString());
        }

        [Fact]
        public void Write_sets_list_the_members_each_method_writes()
        {
            var result = Generate(Usings + @"
public class Order { public int Id { get; set; } public string Name { get; set; } = """"; }
public record OrderDto(int Id) { public string Name { get; set; } = """"; public string? Note { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Note"")] public static partial OrderDto ToDto(Order o);
    public static partial OrderDto ToDto(Order o, string? note);
    public static partial void Update(Order o, Order target);
}");

            AssertClean(result);
            Assert.Contains("[\"ToDto(Order)\"] = new string[] { \"Id\", \"Name\" },", result.Source);
            Assert.Contains("[\"ToDto(Order, string?)\"] = new string[] { \"Id\", \"Name\", \"Note\" },", result.Source);
            Assert.Contains("[\"Update\"] = new string[] { \"Id\", \"Name\" },", result.Source);
            Assert.DoesNotContain("[\"ToDto\"]", result.Source);
        }

        // ------------------------------------------------------------------ generated XML docs

        private const string DocModels = Usings + @"
public class Order { public int Id { get; set; } public string Name { get; set; } = """"; }
public class OrderDto { public int Id { get; set; } public string Title { get; set; } = """"; }
[GeneratedMapper] public static partial class M { [MapMember(""Name"", ""Title"")] public static partial OrderDto ToDto(Order o); }";

        [Fact]
        public void Generated_docs_are_opt_in()
        {
            var without = Generate(DocModels);
            var with = Generate(DocModels, new Dictionary<string, string> { ["ConventionMapperGeneratedDocs"] = "true" });

            Assert.DoesNotContain("cm-doc", without.Source);
            Assert.DoesNotContain("Data flow:", without.Source);
            Assert.Contains("/// <summary>", with.Source);
            Assert.Contains("/// <item><term>Title</term><description>Name</description></item>", with.Source);
            Assert.Contains("/// <item><term>Id</term><description>Id</description></item>", with.Source); // a DTO's Id is not a key
            AssertClean(with);
            Assert.DoesNotContain(with.CompileWarnings, d => d.Id.StartsWith("CS15", System.StringComparison.Ordinal)); // XML doc warnings
        }

        [Fact]
        public void Generated_docs_keep_the_declarations_own_comment()
        {
            var result = Generate(Usings + @"
public class Order { public int Id { get; set; } }
public class OrderDto { public int Id { get; set; } }
[GeneratedMapper] public static partial class M
{
    /// <summary>Maps an order for the API.</summary>
    public static partial OrderDto ToDto(Order o);

    /// <summary>Copies.</summary>
    /// <remarks>Hand-written remarks.</remarks>
    public static partial OrderDto Copy(Order o);
}", new Dictionary<string, string> { ["ConventionMapperGeneratedDocs"] = "true" });

            AssertClean(result);
            var lines = result.Source.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            var summary = lines.IndexOf("/// <summary>Maps an order for the API.</summary>");
            Assert.True(summary >= 0);
            Assert.Equal("/// <remarks>", lines[summary + 1]);
            Assert.Equal("/// Generated by ConventionMapper. Data flow:", lines[summary + 2]);

            var remarks = lines.IndexOf("/// <remarks>Hand-written remarks.</remarks>");
            Assert.True(remarks >= 0);
            Assert.StartsWith("public static partial global::OrderDto Copy(", lines[remarks + 1]); // no generated remarks added
        }

        // ------------------------------------------------------------------ CM0021 / CM0022 projections

        private const string ProjectionModels = Usings + @"
public class Money { public decimal Amount { get; set; } }
public class Order { public int Id { get; set; } public DateTime Created { get; set; } public Money Total { get; set; } = new(); }
public class OrderRow { public string Id { get; set; } = """"; public string Created { get; set; } = """"; public string Total { get; set; } = """"; }
";

        [Fact]
        public void Projection_parts_that_may_not_translate_are_warnings()
        {
            var result = Generate(ProjectionModels + @"
[GeneratedMapper] public static partial class M
{
    public static partial IQueryable<OrderRow> Project(IQueryable<Order> q);
    [MapConversion] private static string Format(Money m) => m.Amount.ToString();
}");

            var warnings = result.WithId("CM0021").Select(d => d.GetMessage()).ToList();
            Assert.Equal(2, warnings.Count);
            Assert.Contains(warnings, m => m.Contains("'OrderRow.Total'") && m.Contains("'Format'"));
            Assert.Contains(warnings, m => m.Contains("'OrderRow.Created'") && m.Contains("ToString() on DateTime"));
            Assert.Empty(result.CompileErrors);
        }

        [Fact]
        public void Provider_hint_tunes_projection_warnings()
        {
            var inMemory = Generate(ProjectionModels + @"
[GeneratedMapper(Provider = ""InMemory"")] public static partial class M
{
    public static partial IQueryable<OrderRow> Project(IQueryable<Order> q);
    [MapConversion] private static string Format(Money m) => m.Amount.ToString();
}");
            var cosmos = Generate(ProjectionModels + @"
[GeneratedMapper(Provider = ""cosmos"")] public static partial class M
{
    [MapIgnore(""Total""), MapIgnore(""Created"")] public static partial IQueryable<OrderRow> Project(IQueryable<Order> q);
}");

            Assert.Empty(inMemory.WithId("CM0021"));
            Assert.Contains(cosmos.WithId("CM0021"), d => d.GetMessage().Contains("ToString() on int") && d.GetMessage().Contains("Cosmos"));
        }

        [Fact]
        public void Unknown_provider_is_a_warning()
        {
            var result = Generate(ProjectionModels + @"
[GeneratedMapper(Provider = ""Oracle9"")] public static partial class M { [MapIgnore(""Total""), MapIgnore(""Created""), MapIgnore(""Id"")] public static partial IQueryable<OrderRow> Project(IQueryable<Order> q); }");

            Assert.Contains(result.WithId("CM0022"), d => d.GetMessage().Contains("Oracle9"));
        }
    }
}
#endif
