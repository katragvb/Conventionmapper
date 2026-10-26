using System;
using System.Linq.Expressions;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public enum OptKind { Unknown, New, Renewal }

    public class OptPolicy { public string Number { get; set; } = ""; }

    public class OptReferral
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Status { get; set; }
        public int? Count { get; set; }
        public OptKind? Kind { get; set; }
        public string Internal { get; set; } = "internal";
    }

    public class OptGap
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "initial";
        public int Count { get; set; } = -1;
        public OptKind Kind { get; set; }
        public int SubmissionNumber { get; set; }
        public string? PolicyNumber { get; set; }
        public string Source { get; set; } = "";
        public string Internal { get; set; } = "unset";
    }

    public record OptGapRecord(int Id, string Name, int SubmissionNumber);

    [GeneratedMapper]
    public static partial class OptMapper
    {
        // Extra parameters fill the destination members with the same name; [MapMember] can point at a parameter too.
        [MapMember("origin", "Source")]
        [MapIgnore("Internal")]
        [MapNullSubstitute("Status", "")]
        [MapNullSubstitute("Count", 0)]
        [MapNullSubstitute("Kind", OptKind.New)]
        public static partial OptGap ToGap(this OptReferral referral, int submissionNumber, string? policyNumber, string origin);

        public static partial OptGapRecord ToRecord(OptReferral referral, int submissionNumber);

        // Explicit mode: only the listed members are copied.
        [MapExplicit("Id", "Name")]
        public static partial OptGap ToSummary(OptReferral referral);

        [MapExplicit("Id")]
        [MapMember("policy.Number", "PolicyNumber")]
        public static partial OptGap WithPolicy(OptReferral referral, OptPolicy? policy);

        [MapExplicit("Name")]
        public static partial void Rename(OptReferral source, OptGap target, int submissionNumber);

        [MapExplicit("Id", "Status")]
        [MapNullSubstitute("Status", "none")]
        public static partial Expression<Func<OptReferral, OptGap>> Projection();
    }

    [GeneratedMapper]
    [MapExplicit]
    public static partial class OptExplicitMapper
    {
        [MapMember("Name", "Name")]
        public static partial OptGap OnlyName(OptReferral referral);

        [MapExplicit("Id")]
        public static partial OptGap IdAndName(OptReferral referral, string name);
    }

    public class GeneratedMapperOptionsTests
    {
        private static readonly OptReferral Referral = new OptReferral { Id = 7, Name = "Ann", Status = null, Count = null, Kind = null };

        [Fact]
        public void Extra_parameters_fill_members_with_the_same_name()
        {
            var gap = Referral.ToGap(submissionNumber: 42, policyNumber: "P-1", origin: "Cusp");

            Assert.Equal(7, gap.Id);
            Assert.Equal("Ann", gap.Name);
            Assert.Equal(42, gap.SubmissionNumber);
            Assert.Equal("P-1", gap.PolicyNumber);
            Assert.Equal("Cusp", gap.Source);
            Assert.Equal("unset", gap.Internal);
        }

        [Fact]
        public void Null_substitutes_replace_null_sources()
        {
            var gap = Referral.ToGap(1, null, "x");
            Assert.Equal("", gap.Status);
            Assert.Equal(0, gap.Count);
            Assert.Equal(OptKind.New, gap.Kind);

            var filled = new OptReferral { Status = "open", Count = 3, Kind = OptKind.Renewal }.ToGap(1, null, "x");
            Assert.Equal("open", filled.Status);
            Assert.Equal(3, filled.Count);
            Assert.Equal(OptKind.Renewal, filled.Kind);
        }

        [Fact]
        public void Extra_parameters_fill_constructor_parameters()
        {
            Assert.Equal(new OptGapRecord(7, "Ann", 9), OptMapper.ToRecord(Referral, 9));
        }

        [Fact]
        public void Explicit_mode_copies_only_listed_members()
        {
            var gap = OptMapper.ToSummary(new OptReferral { Id = 1, Name = "Bo", Status = "open", Count = 5, Internal = "secret" });

            Assert.Equal(1, gap.Id);
            Assert.Equal("Bo", gap.Name);
            Assert.Equal("initial", gap.Status);
            Assert.Equal(-1, gap.Count);
            Assert.Equal("unset", gap.Internal);
        }

        [Fact]
        public void MapMember_paths_can_start_at_a_parameter()
        {
            Assert.Equal("P-9", OptMapper.WithPolicy(Referral, new OptPolicy { Number = "P-9" }).PolicyNumber);
            Assert.Null(OptMapper.WithPolicy(Referral, null).PolicyNumber);
        }

        [Fact]
        public void Update_methods_take_extra_parameters_and_explicit_mode()
        {
            var target = new OptGap { Id = 99, Status = "keep" };

            OptMapper.Rename(Referral, target, 5);

            Assert.Equal("Ann", target.Name);
            Assert.Equal(5, target.SubmissionNumber);
            Assert.Equal(99, target.Id);
            Assert.Equal("keep", target.Status);
        }

        [Fact]
        public void Projections_support_explicit_mode_and_null_substitutes()
        {
            var project = OptMapper.Projection().Compile();

            var gap = project(new OptReferral { Id = 3, Name = "ignored", Status = null });

            Assert.Equal(3, gap.Id);
            Assert.Equal("none", gap.Status);
            Assert.Equal("", gap.Name);
        }

        [Fact]
        public void Explicit_mode_on_the_class_applies_to_every_method()
        {
            var onlyName = OptExplicitMapper.OnlyName(Referral);
            Assert.Equal("Ann", onlyName.Name);
            Assert.Equal(0, onlyName.Id);

            var both = OptExplicitMapper.IdAndName(Referral, "Zed");
            Assert.Equal(7, both.Id);
            Assert.Equal("Zed", both.Name);
        }
    }
}
