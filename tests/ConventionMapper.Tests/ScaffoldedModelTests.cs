#if NET8_0_OR_GREATER
using System.Linq;
using Microsoft.CodeAnalysis;
using static ConventionMapper.Tests.GeneratorHarness;

namespace ConventionMapper.Tests
{
    /// <summary>
    /// Code shaped exactly like `dotnet ef dbcontext scaffold` output (partial classes, no attributes, everything in fluent
    /// configuration: ToTable, HasColumnName, IsUnicode, HasColumnType, composite keys, constraint names, database defaults,
    /// OnModelCreatingPartial). Hand-written test entities don't look like this, which is how 1.9.0 shipped a CM0028 false
    /// positive on every column with a default. Every diagnostic on a typical "copy a quote row into a policy row" map is
    /// listed exactly, so a new false positive fails here.
    /// </summary>
    public class ScaffoldedModelTests
    {
        private const string Scaffolded = @"
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using ConventionMapper;

public partial class TblQuoteForm
{
    public int IntQuoteFormId { get; set; }
    public int IntQuoteId { get; set; }
    public string StrFormName { get; set; } = null!;
    public string? StrEdition { get; set; }
    public string StrAttachmentMethod { get; set; } = null!;
    public bool YsnPrintText { get; set; }
    public bool YsnManual { get; set; }
    public int IntSortSequence { get; set; }
    public DateTime DtmCreated { get; set; }
    public string? Guid { get; set; }
    public virtual TblQuote IntQuote { get; set; } = null!;
}

public partial class TblQuote
{
    public int IntQuoteId { get; set; }
    public virtual ICollection<TblQuoteForm> TblQuoteForms { get; set; } = new List<TblQuoteForm>();
}

public partial class TblPolicyForm
{
    public int IntPolicyId { get; set; }
    public short SrtEndorsementNo { get; set; }
    public string StrFormName { get; set; } = null!;
    public string? StrEdition { get; set; }
    public string StrAttachmentMethod { get; set; } = null!;
    public bool YsnPrintText { get; set; }
    public bool YsnManual { get; set; }
    public int IntSortSequence { get; set; }
    public DateTime DtmCreated { get; set; }
    public byte[] RowVersion { get; set; } = null!;
    public virtual TblPolicy IntPolicy { get; set; } = null!;
}

public partial class TblPolicy
{
    public int IntPolicyId { get; set; }
    public virtual ICollection<TblPolicyForm> TblPolicyForms { get; set; } = new List<TblPolicyForm>();
}

public partial class PolicyContext : DbContext
{
    public PolicyContext(DbContextOptions<PolicyContext> options) : base(options) { }

    public virtual DbSet<TblPolicy> TblPolicies { get; set; }
    public virtual DbSet<TblPolicyForm> TblPolicyForms { get; set; }
    public virtual DbSet<TblQuote> TblQuotes { get; set; }
    public virtual DbSet<TblQuoteForm> TblQuoteForms { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TblPolicyForm>(entity =>
        {
            entity.HasKey(e => new { e.IntPolicyId, e.SrtEndorsementNo, e.StrFormName }).HasName(""PK_tblPolicyForms"");
            entity.ToTable(""tblPolicyForms"");
            entity.Property(e => e.IntPolicyId).HasColumnName(""intPolicyID"");
            entity.Property(e => e.SrtEndorsementNo).HasColumnName(""srtEndorsementNo"");
            entity.Property(e => e.StrFormName).HasMaxLength(32).IsUnicode(false).HasColumnName(""strFormName"");
            entity.Property(e => e.StrEdition).HasMaxLength(10).IsUnicode(false).HasColumnName(""strEdition"");
            entity.Property(e => e.StrAttachmentMethod).IsRequired().HasMaxLength(1).IsUnicode(false).HasDefaultValueSql(""('U')"").IsFixedLength().HasColumnName(""strAttachmentMethod"");
            entity.Property(e => e.YsnPrintText).IsRequired().HasDefaultValueSql(""((1))"").HasColumnName(""ysnPrintText"");
            entity.Property(e => e.YsnManual).HasDefaultValueSql(""((0))"").HasColumnName(""ysnManual"");
            entity.Property(e => e.IntSortSequence).HasColumnName(""intSortSequence"");
            entity.Property(e => e.DtmCreated).HasDefaultValueSql(""(getdate())"").HasColumnType(""datetime"").HasColumnName(""dtmCreated"");
            entity.Property(e => e.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasOne(d => d.IntPolicy).WithMany(p => p.TblPolicyForms)
                .HasForeignKey(d => d.IntPolicyId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName(""FK_tblPolicyForms_tblPolicy"");
        });

        modelBuilder.Entity<TblPolicy>(entity =>
        {
            entity.HasKey(e => e.IntPolicyId);
            entity.ToTable(""tblPolicy"");
            entity.Property(e => e.IntPolicyId).HasColumnName(""intPolicyID"");
        });

        modelBuilder.Entity<TblQuoteForm>(entity =>
        {
            entity.HasKey(e => e.IntQuoteFormId);
            entity.ToTable(""tblQuoteForms"");
            entity.HasIndex(e => e.IntQuoteId, ""IX_tblQuoteForms_intQuoteID"");
            entity.Property(e => e.IntQuoteFormId).HasColumnName(""intQuoteFormID"");
            entity.Property(e => e.StrFormName).HasMaxLength(255).IsUnicode(false).HasColumnName(""strFormName"");
            entity.Property(e => e.StrEdition).HasMaxLength(10).IsUnicode(false).HasColumnName(""strEdition"");
            entity.Property(e => e.StrAttachmentMethod).HasMaxLength(1).IsUnicode(false).IsFixedLength();
            entity.Property(e => e.Guid).HasMaxLength(36).HasColumnName(""guid"");
            entity.HasOne(d => d.IntQuote).WithMany(p => p.TblQuoteForms)
                .HasForeignKey(d => d.IntQuoteId)
                .HasConstraintName(""FK_tblQuoteForms_tblQuote"");
        });

        modelBuilder.Entity<TblQuote>(entity =>
        {
            entity.HasKey(e => e.IntQuoteId);
            entity.ToTable(""tblQuote"");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
";

        [Fact]
        public void Copying_a_scaffolded_row_reports_only_real_problems()
        {
            var result = Generate(Scaffolded + @"
[GeneratedMapper, MapExplicit]
public static partial class Mappings
{
    // Keys from the caller; content from the quote row.
    [MapExplicit(""StrFormName"", ""StrEdition"", ""StrAttachmentMethod"", ""YsnPrintText"", ""YsnManual"", ""DtmCreated"")]
    [MapMember(""policyId"", ""IntPolicyId"")]
    [MapMember(""endorsementNo"", ""SrtEndorsementNo"")]
    [MapMember(""sortSequence"", ""IntSortSequence"")]
    [MapReviewed(""IntPolicyId"", ""SrtEndorsementNo"", Reason = ""the caller owns the policy row's key"")]
    public static partial TblPolicyForm ToPolicyForm(this TblQuoteForm source, int policyId, short endorsementNo, int sortSequence);
}");

            Assert.Empty(result.CompileErrors);
            var reported = result.Generator.Where(d => d.Severity >= DiagnosticSeverity.Info)
                .Select(d => d.Id + " " + d.GetMessage()).OrderBy(m => m, System.StringComparer.Ordinal).ToList();

            Assert.Collection(reported,
                // 255 characters on the quote, 32 on the policy: a real length problem.
                m => Assert.StartsWith("CM0027 'TblQuoteForm.StrFormName' can be longer than 32 characters", m),
                // A copied false is saved as the default 1: a real data problem.
                m => Assert.StartsWith("CM0034 'ToPolicyForm' writes 'YsnPrintText' of the entity 'TblPolicyForm'", m));
            // Not reported: the defaults on StrAttachmentMethod (a string), YsnManual (0, the CLR default) and DtmCreated
            // (a DateTime's CLR default means "not set"), the reviewed key members, and the row version and navigations,
            // which the map doesn't write.
            Assert.Contains("the caller owns the policy row's key", result.Report);
            Assert.Contains("`TblPolicyForm.StrFormName` (key) is written from `StrFormName`", result.Report); // key part, not reviewed
        }

        [Fact]
        public void One_acknowledgement_covers_every_warning_on_its_member()
        {
            // StrFormName is both a key part and too long: acknowledging the key write also acknowledges the length.
            var result = Generate(Scaffolded + @"
[GeneratedMapper, MapExplicit]
public static partial class Mappings
{
    [MapExplicit(""StrFormName"")]
    [MapMember(""policyId"", ""IntPolicyId"")]
    [MapMember(""endorsementNo"", ""SrtEndorsementNo"")]
    [MapReviewed(""IntPolicyId"", ""SrtEndorsementNo"", ""StrFormName"")]
    public static partial TblPolicyForm ToPolicyForm(this TblQuoteForm source, int policyId, short endorsementNo);
}");

            Assert.Empty(result.WithId("CM0027"));
            Assert.Empty(result.WithId("CM0035"));
            Assert.Contains("key (reviewed)", result.Report);
        }

        [Fact]
        public void A_kind_limits_an_acknowledgement_to_that_warning()
        {
            var result = Generate(Scaffolded + @"
[GeneratedMapper, MapExplicit]
public static partial class Mappings
{
    [MapExplicit(""StrFormName"")]
    [MapMember(""policyId"", ""IntPolicyId"")]
    [MapMember(""endorsementNo"", ""SrtEndorsementNo"")]
    [MapReviewed(""IntPolicyId"", ""SrtEndorsementNo"", ""StrFormName"", For = ReviewKind.Key)]
    public static partial TblPolicyForm ToPolicyForm(this TblQuoteForm source, int policyId, short endorsementNo);
}");

            Assert.Empty(result.CompileErrors);
            Assert.Contains("key (reviewed)", result.Report);
            Assert.Single(result.WithId("CM0027"));   // the length is a separate problem
            Assert.Empty(result.WithId("CM0035"));
            Assert.Contains("`IntPolicyId`, `SrtEndorsementNo`, `StrFormName` (key)", result.Report);
        }

        [Fact]
        public void An_acknowledgement_of_a_kind_that_never_occurs_is_stale()
        {
            var result = Generate(Scaffolded + @"
[GeneratedMapper, MapExplicit]
public static partial class Mappings
{
    [MapExplicit(""StrFormName"")]
    [MapMember(""policyId"", ""IntPolicyId"")]
    [MapMember(""endorsementNo"", ""SrtEndorsementNo"")]
    [MapReviewed(""IntPolicyId"", ""SrtEndorsementNo"", For = ReviewKind.Key)]
    [MapReviewed(""StrFormName"", For = ReviewKind.Key | ReviewKind.Length)]
    [MapReviewed(""StrFormName"", For = ReviewKind.Navigation)]
    public static partial TblPolicyForm ToPolicyForm(this TblQuoteForm source, int policyId, short endorsementNo);
}");

            Assert.Empty(result.CompileErrors);
            Assert.Empty(result.WithId("CM0027"));
            var stale = Assert.Single(result.WithId("CM0035"));
            Assert.Contains("[MapReviewed(\"StrFormName\", For = Navigation)] on 'ToPolicyForm'", stale.GetMessage());
        }
    }
}
#endif
