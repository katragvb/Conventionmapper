#if NET8_0_OR_GREATER
using System.Linq;
using Microsoft.CodeAnalysis;
using static ConventionMapper.Tests.GeneratorHarness;

namespace ConventionMapper.Tests
{
    /// <summary>[MapConstant], [MapFrom], [MapRequired], [MapTruncate], the EF model from fluent configuration, CM0027 and CM0028.</summary>
    public class GeneratorValueTests
    {
        private const string Usings = "using System; using System.Collections.Generic; using System.Linq; using System.Linq.Expressions; using System.ComponentModel.DataAnnotations; using ConventionMapper;\n";

        private const string Models = Usings + @"
public enum Kind { None, A, B }
public class Src { public int Id { get; set; } public string Name { get; set; } = """"; public string? Note { get; set; } public int Count { get; set; } }
public class Dst { public int Id { get; set; } public string Name { get; set; } = """"; public string? Note { get; set; } public int Count { get; set; } public Kind Kind { get; set; } public long Total { get; set; } }
";

        [Fact]
        public void Constants_are_checked_and_reported()
        {
            var result = Generate(Models + @"
[GeneratedMapper] public static partial class M
{
    [MapConstant(""Kind"", Kind.B), MapConstant(""Total"", 5), MapConstant(""Note"", null)]
    public static partial Dst Ok(Src s);

    [MapConstant(""Kind"", ""B""), MapConstant(""Id"", null), MapConstant(""Missing"", 1)]
    public static partial Dst Bad(Src s);
}");

            Assert.Empty(result.CompileErrors);
            var ok = result.Source.Substring(result.Source.IndexOf("Ok(", System.StringComparison.Ordinal));
            Assert.Contains("Total = ((int)(5))", ok);
            Assert.Contains("Note = default(string)", ok);
            Assert.Contains("| Kind | constant `Kind.B` | constant |", result.Report);
            var errors = result.WithId("CM0025").Select(d => d.GetMessage()).ToList();
            Assert.Contains(errors, m => m.Contains("[MapConstant] for 'Kind' on 'Bad'") && m.Contains("converts implicitly to Kind"));
            Assert.Contains(errors, m => m.Contains("[MapConstant] for 'Id' on 'Bad'") && m.Contains("int cannot be null"));
            Assert.Contains(result.WithId("CM0008"), d => d.GetMessage().Contains("'Missing' in [MapConstant]"));
        }

        [Fact]
        public void MapFrom_finds_the_method_and_passes_extra_parameters()
        {
            var result = Generate(Models + @"
[GeneratedMapper] public static partial class M
{
    [MapFrom(nameof(Upper), ""Name""), MapFrom(nameof(Plus), ""Count""), MapIgnore(""Kind""), MapIgnore(""Total"")]
    public static partial Dst Map(Src s, int extra);

    [MapFrom(""Nope"", ""Name""), MapFrom(nameof(Wrong), ""Count""), MapIgnore(""Kind""), MapIgnore(""Total"")]
    public static partial Dst Bad(Src s);

    private static string Upper(Src s) => s.Name.ToUpperInvariant();
    private static int Plus(Src s, int extra) => s.Count + extra;
    private static Guid Wrong(Src s) => Guid.Empty;
}");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("Name = Upper(s)", result.Source);
            Assert.Contains("Count = Plus(s, extra)", result.Source);
            Assert.Empty(result.WithId("CM0011"));   // extra is used by Plus
            Assert.Empty(result.WithId("CM0017"));   // [MapFrom] methods are not conversions
            var errors = result.WithId("CM0025").Select(d => d.GetMessage()).ToList();
            Assert.Contains(errors, m => m.Contains("needs a static method 'Nope' that takes Src"));
            Assert.Contains(errors, m => m.Contains("'Wrong' returns Guid, which cannot be converted to int"));
        }

        [Fact]
        public void MapFrom_in_a_projection_is_inlined_when_it_is_one_expression()
        {
            var result = Generate(Models + @"
[GeneratedMapper] public static partial class M
{
    [MapFrom(nameof(Short), ""Name""), MapFrom(nameof(Block), ""Note""), MapIgnore(""Kind""), MapIgnore(""Total"")]
    public static partial IQueryable<Dst> Project(this IQueryable<Src> q);

    private static string Short(Src s) => s.Name.Length > 3 ? string.Concat(s.Name.Take(3)) : s.Name;
    private static string? Block(Src s) { var n = s.Note; return n; }
}");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("Name = ((x).Name.Length > 3 ? string.Concat((x).Name.Take(3)) : (x).Name)", result.Source);
            Assert.Contains("using System.Linq;", result.Source); // the extension method Take needs it
            Assert.Contains("Note = Block(x)", result.Source);
            Assert.Contains(result.WithId("CM0021"), d => d.GetMessage().Contains("'Block'") && d.GetMessage().Contains("not a single expression"));
        }

        [Fact]
        public void Required_and_truncate_problems_are_reported()
        {
            var result = Generate(Models + @"
[GeneratedMapper] public static partial class M
{
    [MapRequired(""Count""), MapRequired(""Note""), MapNullSubstitute(""Note"", """"), MapTruncate(""Name""), MapIgnore(""Kind""), MapIgnore(""Total"")]
    public static partial Dst Map(Src s);

    [MapRequired(""Note""), MapIgnore(""Kind""), MapIgnore(""Total"")]
    public static partial IQueryable<Dst> Project(IQueryable<Src> q);
}");

            var errors = result.WithId("CM0025").Select(d => d.GetMessage()).ToList();
            Assert.Contains(errors, m => m.Contains("[MapRequired] for 'Count'") && m.Contains("can never be null"));
            Assert.Contains(errors, m => m.Contains("[MapRequired] for 'Note'") && m.Contains("[MapNullSubstitute]"));
            Assert.Contains(errors, m => m.Contains("[MapTruncate] for 'Name'") && m.Contains("no maximum length"));
            Assert.Contains(errors, m => m.Contains("[MapRequired] for 'Note' on 'Project'") && m.Contains("projection cannot throw"));
        }

        // ------------------------------------------------------------------ EF model from fluent configuration

        private const string EfModels = Usings + @"
using Microsoft.EntityFrameworkCore;
public class Owner { public int Id { get; set; } }
public class Location
{
    public string Code { get; set; } = """";
    public int Number { get; set; }
    public string City { get; set; } = """";
    public int OwnerRef { get; set; }
    public Owner? Owner { get; set; }
}
public class LocationDto { public string Code { get; set; } = """"; public int Number { get; set; } public string City { get; set; } = """"; public int OwnerRef { get; set; } }
public class Db : DbContext
{
    public DbSet<Location> Locations => Set<Location>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.Code);
            entity.Property(e => e.City).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Number).ValueGeneratedOnAdd();
            entity.HasOne(d => d.Owner).WithMany().HasForeignKey(d => d.OwnerRef);
        });
    }
}
";

        [Fact]
        public void The_EF_model_supplies_keys_lengths_and_generated_values()
        {
            var result = Generate(EfModels + @"
[GeneratedMapper] public static partial class M { [MapIgnore(""Owner"")] public static partial Location ToEntity(LocationDto dto); }");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("`Location.Code` (key)", result.Report);        // HasKey
            Assert.Contains("`Location.OwnerRef` (key)", result.Report);    // HasForeignKey
            var generated = Assert.Single(result.WithId("CM0028"));          // ValueGeneratedOnAdd
            Assert.Contains("the database-generated key 'Number'", generated.GetMessage());
            var length = Assert.Single(result.WithId("CM0027"));             // HasMaxLength(20), entity: warning
            Assert.Equal(DiagnosticSeverity.Warning, length.Severity);
            Assert.Contains("can be longer than 20 characters, the maximum of 'Location.City'", length.GetMessage());
        }

        [Fact]
        public void Truncate_uses_the_EF_length_and_reviewed_members_are_quiet()
        {
            var result = Generate(EfModels + @"
[GeneratedMapper] public static partial class M
{
    [MapIgnore(""Owner""), MapTruncate(""City""), MapReviewed(""Number"")]
    public static partial Location ToEntity(LocationDto dto);
}");

            Assert.Empty(result.WithId("CM0027"));
            Assert.Empty(result.WithId("CM0028"));
            Assert.Contains("City = __Truncate(dto.City, 20)", result.Source);
            Assert.Contains("generated key (reviewed)", result.Report);
        }

        [Fact]
        public void Writing_a_navigation_of_an_entity_is_a_warning()
        {
            var result = Generate(EfModels + @"
public class OwnerDto { public int Id { get; set; } }
public class LocationWithOwner { public string Code { get; set; } = """"; public int Number { get; set; } public string City { get; set; } = """"; public int OwnerRef { get; set; } public OwnerDto? Owner { get; set; } }
[GeneratedMapper] public static partial class M
{
    [MapReviewed(""Number""), MapTruncate(""City"")]
    public static partial Location ToEntity(LocationWithOwner dto);
}");

            var warnings = result.WithId("CM0028").Select(d => d.GetMessage()).ToList();
            Assert.Contains(warnings, m => m.Contains("writes the navigation 'Owner' of the entity 'Location'"));
            // The navigation also creates an Owner, whose generated Id is copied too.
            Assert.Contains(warnings, m => m.Contains("writes the database-generated key 'Id' of the entity 'Owner'."));
        }

        [Fact]
        public void Length_on_a_DTO_is_only_information()
        {
            var result = Generate(Models + @"
public class Limited { [MaxLength(10)] public string Name { get; set; } = """"; }
public class Short { [StringLength(5)] public string Name { get; set; } = """"; }
[GeneratedMapper] public static partial class M
{
    public static partial Limited ToLimited(Src s);
    public static partial Limited FromShort(Short s);
}");

            var info = Assert.Single(result.WithId("CM0027"));   // FromShort: 5 <= 10, no diagnostic
            Assert.Equal(DiagnosticSeverity.Info, info.Severity);
            Assert.Contains("'Src.Name'", info.GetMessage());
        }
    }
}
#endif
