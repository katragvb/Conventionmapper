#if NET8_0_OR_GREATER
using System.Linq;
using Microsoft.CodeAnalysis;
using static ConventionMapper.Tests.GeneratorHarness;

namespace ConventionMapper.Tests
{
    /// <summary>CM0034 (a database default replaces the CLR default on insert), CM0035 (stale [MapReviewed]) and review reasons.</summary>
    public class GeneratorReviewTests
    {
        private const string FormModel = "using System; using ConventionMapper; using Microsoft.EntityFrameworkCore;\n" + @"
public class Form
{
    public int Id { get; set; }
    public bool PrintText { get; set; }
    public int Copies { get; set; }
    public int Pages { get; set; }
    public string Method { get; set; } = """";
    public bool? Optional { get; set; }
    public bool Flagged { get; set; }
}
public class FormDto
{
    public bool PrintText { get; set; }
    public int Copies { get; set; }
    public int Pages { get; set; }
    public string Method { get; set; } = """";
    public bool? Optional { get; set; }
    public bool Flagged { get; set; }
}
public class Db : DbContext
{
    public DbSet<Form> Forms => Set<Form>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Form>(entity =>
        {
            entity.Property(e => e.PrintText).IsRequired().HasDefaultValueSql(""((1))"");
            entity.Property(e => e.Copies).HasDefaultValue(1);
            entity.Property(e => e.Pages).HasDefaultValueSql(""((0))"");
            entity.Property(e => e.Method).HasDefaultValueSql(""('U')"");
            entity.Property(e => e.Optional).HasDefaultValueSql(""((1))"");
            entity.Property(e => e.Flagged).HasDefaultValueSql(""((1))"").HasSentinel(true);
        });
    }
}
";

        [Fact]
        public void A_value_type_with_a_database_default_loses_its_CLR_default_on_insert()
        {
            var result = Generate(FormModel + @"
[GeneratedMapper] public static partial class M { [MapIgnore(""Id"")] public static partial Form ToEntity(FormDto dto); }");

            Assert.Empty(result.CompileErrors);
            var messages = result.WithId("CM0034").Select(d => d.GetMessage()).OrderBy(m => m).ToList();
            // Pages defaults to 0 (the CLR default), Method is a string, Optional is nullable and Flagged has a sentinel.
            Assert.Equal(2, messages.Count);
            Assert.Contains("'Copies' of the entity 'Form'", messages[0]);
            Assert.Contains("is 0", messages[0]);
            Assert.Contains("'PrintText' of the entity 'Form'", messages[1]);
            Assert.Contains("is false", messages[1]);
            Assert.All(result.WithId("CM0034"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
            Assert.Contains("`Form.PrintText` has a database default", result.Report);
        }

        [Fact]
        public void A_copied_null_that_becomes_the_database_default_is_information()
        {
            var result = Generate("using System; using ConventionMapper; using Microsoft.EntityFrameworkCore;\n" + @"
public class Row { public int Id { get; set; } public bool? PrintText { get; set; } public string? Method { get; set; } public string Code { get; set; } = """"; public int? Copies { get; set; } }
public class Input { public bool? PrintText { get; set; } public string? Method { get; set; } public string Code { get; set; } = """"; public int Copies { get; set; } }
public class Db : DbContext
{
    public DbSet<Row> Rows => Set<Row>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Row>(entity =>
        {
            entity.Property(e => e.PrintText).HasDefaultValueSql(""((1))"");
            entity.Property(e => e.Method).HasDefaultValueSql(""('U')"");
            entity.Property(e => e.Code).HasDefaultValueSql(""('X')"");
            entity.Property(e => e.Copies).HasDefaultValueSql(""((1))"");
        });
}
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Id"")] public static partial Row ToRow(Input input);

    [MapIgnore(""Id""), MapReviewed(""Method"", For = ReviewKind.DatabaseDefault)] public static partial Row ToReviewedRow(Input input);
}");

            Assert.Empty(result.CompileErrors);
            // Code and Copies can't be null in the source, so their defaults never apply.
            var messages = result.WithId("CM0036").Select(d => d.GetMessage()).OrderBy(m => m, System.StringComparer.Ordinal).ToList();
            Assert.Equal(3, messages.Count);
            Assert.StartsWith("'ToReviewedRow' writes 'PrintText' of the entity 'Row'", messages[0]);   // Method is reviewed there
            Assert.StartsWith("'ToRow' writes 'Method' of the entity 'Row'", messages[1]);
            Assert.Contains("a copied null is stored as U", messages[1]);
            Assert.StartsWith("'ToRow' writes 'PrintText' of the entity 'Row'", messages[2]);
            Assert.All(result.WithId("CM0036"), d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
            Assert.Empty(result.WithId("CM0035"));
        }

        [Fact]
        public void Update_methods_and_reviewed_members_are_quiet()
        {
            var result = Generate(FormModel + @"
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Id""), MapReviewed(""PrintText"", ""Copies"")]
    public static partial Form ToEntity(FormDto dto);

    [MapIgnore(""Id"")]
    public static partial void Update(FormDto dto, Form form);
}");

            Assert.Empty(result.WithId("CM0034"));
            Assert.Empty(result.WithId("CM0035")); // both acknowledgements are needed
            Assert.Contains("database default (reviewed)", result.Report);
        }

        private const string PolicyModel = "using System; using ConventionMapper; using Microsoft.EntityFrameworkCore;\n" + @"
public class Policy { public int PolicyId { get; set; } public int ProgramId { get; set; } public string Name { get; set; } = """"; }
public class PolicyRow { public int PolicyId { get; set; } public int ProgramId { get; set; } public string Name { get; set; } = """"; }
public class Db : DbContext
{
    public DbSet<PolicyRow> Rows => Set<PolicyRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<PolicyRow>(entity => entity.HasKey(e => new { e.PolicyId, e.ProgramId }));
}
";

        [Fact]
        public void An_acknowledgement_that_silences_nothing_is_stale()
        {
            var result = Generate(PolicyModel + @"
[GeneratedMapper, MapReviewed(""Name"")]
public static partial class M
{
    [MapReviewed(""PolicyId"", ""ProgramId"", ""Name"")]
    public static partial PolicyRow ToRow(Policy policy);
}");

            Assert.Empty(result.CompileErrors);
            var stale = result.WithId("CM0035").Select(d => d.GetMessage()).OrderBy(m => m).ToList();
            Assert.Equal(2, stale.Count);
            Assert.Contains("[MapReviewed(\"Name\")] on 'M'", stale[0]);       // the class
            Assert.Contains("[MapReviewed(\"Name\")] on 'ToRow'", stale[1]);   // the method; its key acknowledgements are used
            Assert.Equal(DiagnosticSeverity.Warning, result.WithId("CM0035").First().Severity);
        }

        [Fact]
        public void An_acknowledgement_used_by_an_inheriting_method_is_not_stale()
        {
            var result = Generate(PolicyModel + @"
[GeneratedMapper] public static partial class M
{
    // ToName writes no key itself; ToFull inherits its acknowledgement and needs it.
    [MapExplicit(""Name""), MapReviewed(""PolicyId"", ""ProgramId"")]
    public static partial PolicyRow ToName(Policy policy);

    [MapInherit(nameof(ToName)), MapExplicit(""PolicyId"", ""ProgramId"")]
    public static partial PolicyRow ToFull(Policy policy);
}");

            Assert.Empty(result.CompileErrors);
            Assert.Empty(result.WithId("CM0035"));
        }

        [Fact]
        public void Reasons_are_listed_in_the_report()
        {
            var result = Generate(PolicyModel + @"
[GeneratedMapper] public static partial class M
{
    [MapReviewed(""PolicyId"", ""ProgramId"", Reason = ""the caller copies a quote into its own policy"")]
    public static partial PolicyRow ToRow(Policy policy);
}");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("Acknowledged with [MapReviewed]:", result.Report);
            Assert.Contains("`ToRow(Policy)`: `PolicyId`, `ProgramId`: the caller copies a quote into its own policy", result.Report);
        }
    }
}
#endif
