using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using ConventionMapper;
using ConventionMapper.Testing;

namespace ConventionMapper.Tests.TestingPackage
{
    public enum TpKind { None, Office, Warehouse }

    public class TpAddress { public string City { get; set; } = ""; }

    public class TpLocation
    {
        public int LocationNumber { get; set; }
        public string Street1 { get; set; } = "";
        public TpAddress Address { get; set; } = new TpAddress();
        public TpKind Kind { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string Internal { get; set; } = "";
    }

    public class TpImport
    {
        public int LocationNumber { get; set; }
        public string Street1 { get; set; } = "";
        public string AddressCity { get; set; } = "";
        public TpKind Kind { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string? GlClassCode { get; set; }
        public decimal? Exposure { get; set; }
        public string Internal { get; set; } = "";
    }

    public record TpSummary(int LocationNumber, string Street1);

    [GeneratedMapper]
    public static partial class TpMappings
    {
        [MapExplicit("LocationNumber", "Street1", "AddressCity", "Kind", "Tags")]
        public static partial TpImport ToImport(this TpLocation source);

        // Same lists as ToImport, plus the extra parameters.
        [MapInherit(nameof(ToImport))]
        public static partial TpImport ToImport(this TpLocation source, string? glClassCode, decimal? exposure);

        public static partial TpSummary ToSummary(TpLocation source);

        [MapIgnore("Internal")]
        public static partial void Update(TpLocation source, TpLocation target);

        // ToImport has two overloads: name the one to inherit from.
        [MapInherit(nameof(ToImport), typeof(TpLocation))]
        public static partial Expression<Func<TpLocation, TpImport>> Projection();
    }

    [GeneratedMapper]
    public static partial class TpUntested
    {
        public static partial TpSummary Covered(TpLocation source);
        public static partial TpSummary NeverCalled(TpLocation source);
    }

    public class TestingPackageTests
    {
        [Fact]
        public void Generated_write_sets_match_real_behavior()
        {
            MapperAssert.WritesExactly((TpLocation s) => s.ToImport(), TpMappings.WriteSets["ToImport(TpLocation)"]);
            MapperAssert.WritesExactly((TpLocation s) => s.ToImport("GL1", 10m), TpMappings.WriteSets["ToImport(TpLocation, string?, decimal?)"]);
            MapperAssert.WritesExactly((TpLocation s) => TpMappings.ToSummary(s), MapperAssert.WriteSetOf(typeof(TpMappings), "ToSummary"));
        }

        [Fact]
        public void MapInherit_overload_writes_the_inherited_list_plus_parameters()
        {
            MapperAssert.WritesExactly((TpLocation s) => s.ToImport("GL1", 10m),
                "LocationNumber", "Street1", "AddressCity", "Kind", "Tags", "GlClassCode", "Exposure");
            Assert.DoesNotContain("Internal", TpMappings.WriteSets["ToImport(TpLocation, string?, decimal?)"]);
        }

        [Fact]
        public void WritesExactly_reports_unexpected_and_missing_members()
        {
            var error = Assert.Throws<MapperAssertException>(() =>
                MapperAssert.WritesExactly((TpLocation s) => s.ToImport(), "LocationNumber", "Street1", "Internal"));

            Assert.Contains("TpLocation → TpImport", error.Message);
            Assert.Contains("Written but not expected: AddressCity, Kind, Tags", error.Message);
            Assert.Contains("Expected but not written: Internal", error.Message);
        }

        [Fact]
        public void UpdatesExactly_checks_update_methods()
        {
            MapperAssert.UpdatesExactly((TpLocation s, TpLocation t) => TpMappings.Update(s, t), TpMappings.WriteSets["Update"]);
            Assert.Throws<MapperAssertException>(() =>
                MapperAssert.UpdatesExactly((TpLocation s, TpLocation t) => TpMappings.Update(s, t), new[] { "LocationNumber" }));
        }

        [Fact]
        public void Ignore_leaves_members_out_of_the_comparison()
        {
            MapperAssert.WritesExactly((TpLocation s) => s.ToImport(), new[] { "LocationNumber", "Street1", "AddressCity", "Kind" }, ignore: new[] { "Tags" });
        }

        [Fact]
        public void Every_mapping_method_has_a_test()
        {
            MapperAssert.AllMethodsCovered(typeof(TpMappings), typeof(TestingPackageTests).Assembly);
        }

        [Fact]
        public void Uncalled_mapping_methods_are_listed()
        {
            _ = TpUntested.Covered(new TpLocation());

            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.AllMethodsCovered(typeof(TpUntested), typeof(TestingPackageTests).Assembly));

            Assert.Contains("TpUntested.NeverCalled(TpLocation)", error.Message);
            Assert.DoesNotContain("Covered(", error.Message.Replace("NeverCalled", ""));
        }

        [Fact]
        public void Every_mapping_method_is_verified_by_a_write_check()
        {
            MapperAssert.AllMethodsVerified(typeof(TpMappings), typeof(TestingPackageTests).Assembly);
        }

        [Fact]
        public void A_call_without_a_write_check_does_not_count_as_verified()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.AllMethodsVerified(typeof(TpUntested), typeof(TestingPackageTests).Assembly));

            Assert.Contains("TpUntested.Covered(TpLocation) (called, but not in WritesExactly/UpdatesExactly)", error.Message);
            Assert.Contains("TpUntested.NeverCalled(TpLocation) (never called)", error.Message);
        }

        [Fact]
        public void Typed_write_set_lookup_follows_the_signature()
        {
            Assert.Equal(TpMappings.WriteSets["ToImport(TpLocation, string?, decimal?)"],
                TpMappings.WriteSetOf(nameof(TpMappings.ToImport), typeof(TpLocation), typeof(string), typeof(decimal?)));
            Assert.Equal(TpMappings.WriteSets["ToSummary"], TpMappings.WriteSetOf(nameof(TpMappings.ToSummary)));
            Assert.Equal(TpMappings.WriteSets["Update"], MapperAssert.WriteSetOf(typeof(TpMappings), nameof(TpMappings.Update), typeof(TpLocation), typeof(TpLocation)));

            // A changed signature fails with the current overloads instead of silently finding nothing.
            var changed = Assert.Throws<ArgumentException>(() => TpMappings.WriteSetOf(nameof(TpMappings.ToImport), typeof(TpLocation), typeof(int)));
            Assert.Contains("Overloads: ToImport(TpLocation), ToImport(TpLocation, string?, decimal?)", changed.Message);
            Assert.Throws<ArgumentException>(() => TpMappings.WriteSetOf(nameof(TpMappings.ToImport))); // overloaded: types needed
            var viaAssert = Assert.Throws<MapperAssertException>(() => MapperAssert.WriteSetOf(typeof(TpMappings), "Missing", typeof(int)));
            Assert.Contains("No generated mapping method is named 'Missing'", viaAssert.Message);
        }

        [Fact]
        public void Projection_inherits_the_explicit_list()
        {
            var source = SampleData.Create<TpLocation>();
            var row = new[] { source }.AsQueryable().Select(TpMappings.Projection()).Single();

            Assert.Equal(source.Street1, row.Street1);
            Assert.Equal(source.Address.City, row.AddressCity);
            Assert.Equal("", row.Internal); // not in the inherited [MapExplicit] list
        }

        [Fact]
        public void Sample_data_fills_every_member_with_distinct_values()
        {
            var location = SampleData.Create<TpLocation>();

            Assert.StartsWith("Street1-", location.Street1);
            Assert.StartsWith("City-", location.Address.City);
            Assert.Equal(TpKind.Office, location.Kind);
            Assert.Single(location.Tags);
            Assert.NotEqual(0, location.LocationNumber);

            var summary = SampleData.Create<TpSummary>();
            Assert.StartsWith("Street1-", summary.Street1);
        }

        public class Node { public string Name { get; set; } = ""; public Node? Parent { get; set; } public Dictionary<string, int> Counts { get; set; } = new(); public int[] Values { get; set; } = Array.Empty<int>(); }

        [Fact]
        public void Sample_data_stops_at_recursive_types_and_fills_dictionaries_and_arrays()
        {
            var node = SampleData.Create<Node>();

            Assert.Null(node.Parent);
            Assert.Single(node.Counts);
            Assert.Single(node.Values);
        }
    }
}
