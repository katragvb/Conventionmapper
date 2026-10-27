using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;
using ConventionMapper.Testing;

namespace ConventionMapper.Tests.TestingPackage
{
    public enum TbStatus { Open, Submitted, Closed }

    public class TbUser { public int Id { get; set; } public string Name { get; set; } = ""; }

    public class TbClaim
    {
        public int Number { get; set; }
        public TbUser SubmitUser { get; set; } = new TbUser();
        public TbUser CompleteUser { get; set; } = new TbUser();
        public string? Note { get; set; }
        public TbStatus Status { get; set; }
        public long Amount { get; set; }
    }

    public class TbClaimDto
    {
        public int Number { get; set; }
        public int SubmitUserId { get; set; }
        public int CompleteUserId { get; set; }
        public string? Note { get; set; }
        public TbStatus Status { get; set; }
        public long Amount { get; set; }
    }

    public class TbAddress { public string Street { get; set; } = ""; public string? Zip { get; set; } }
    public class TbAddressDto { public string Street { get; set; } = ""; public string? Zip { get; set; } }

    public class TbPerson
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Nickname { get; set; }
        public int? Age { get; set; }
        public TbAddress Address { get; set; } = new TbAddress();
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class TbPersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Nickname { get; set; }
        public int? Age { get; set; }
        public TbAddressDto Address { get; set; } = new TbAddressDto();
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class TbPersonLite { public int Id { get; set; } public string Name { get; set; } = ""; public TbAddressDto Address { get; set; } = new TbAddressDto(); public List<string> Tags { get; set; } = new List<string>(); public int? Age { get; set; } }

    [GeneratedMapper]
    public static partial class TbMappings
    {
        public static partial TbClaimDto ToDto(TbClaim source);
        public static partial TbClaimDto ToDto(TbClaim source, string? note);
        public static partial void Update(TbClaimDto source, TbClaimDto target);
        public static partial TbPersonDto ToDto(TbPerson source);
        public static partial TbPerson ToPerson(TbPersonDto source);
        public static partial TbPersonLite ToLite(TbPerson source);
        [MapIgnore(nameof(TbPerson.Nickname))]
        public static partial TbPerson FromLite(TbPersonLite source);
        public static partial TbAddressDto ToDto(TbAddress source);
        public static partial TbAddress ToAddress(TbAddressDto source);
    }

    public class TbCode { public string Code { get; set; } = ""; }
    public class TbCodeDto { public int Code { get; set; } }

    [GeneratedMapper]
    public static partial class TbBroken
    {
        [MapConversion]
        public static int ToNumber(string text) => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        public static partial TbCodeDto ToDto(TbCode source);
    }

    public class TbOrderLine { public string Sku { get; set; } = ""; }
    public class TbOrderLineDto { public string Sku { get; set; } = ""; }
    public class TbOrder { public int Id { get; set; } public TbOrderLine Line { get; set; } = new TbOrderLine(); }
    public class TbOrderDto { public int Id { get; set; } public TbOrderLineDto Line { get; set; } = new TbOrderLineDto(); }

    [GeneratedMapper]
    public static partial class TbUsage
    {
        public static partial TbOrderDto Used(TbOrder source);
        public static partial TbOrderLineDto NestedOnly(TbOrderLine source); // only called by Used's generated code
        public static partial TbOrderDto Unused(TbOrder source);
    }

    /// <summary>Stands in for production code: the only caller of TbUsage.Used.</summary>
    public static class TbService
    {
        public static TbOrderDto Load(TbOrder order) => TbUsage.Used(order);
    }

    public sealed class TbGuarded
    {
        private TbGuarded() { }
        public string Name { get; private set; } = "";
        public int Count { get; set; }
    }

    public interface ITbShape { string Label { get; set; } }
    public sealed class TbSquare : ITbShape { public string Label { get; set; } = ""; public int Side { get; set; } }
    public sealed class TbHasShape { public ITbShape? Shape { get; set; } }

    public sealed class TbNoDefault
    {
        public TbNoDefault(Guid key, string label) { Key = key; Label = label; }
        public Guid Key { get; }
        public string Label { get; }
    }

    public record TbZipRecord(string Zip, string City);

    public class TestingBehaviorTests
    {
        // ------------------------------------------------------------------ WritesFrom

        [Fact]
        public void Generated_lineage_is_an_executable_test()
        {
            var lineage = MapperAssert.LineageOf(typeof(TbMappings), nameof(TbMappings.ToDto), typeof(TbClaim));
            Assert.Equal("SubmitUser.Id", lineage["SubmitUserId"]);
            Assert.Equal("CompleteUser.Id", lineage["CompleteUserId"]);
            Assert.False(MapperAssert.LineageOf(typeof(TbMappings), nameof(TbMappings.ToDto), typeof(TbClaim), typeof(string)).ContainsKey("Note")); // a parameter

            MapperAssert.WritesFrom((TbClaim c) => TbMappings.ToDto(c), lineage);
        }

        [Fact]
        public void A_swapped_pair_names_where_the_value_came_from()
        {
            var lineage = MapperAssert.LineageOf(typeof(TbMappings), nameof(TbMappings.ToDto), typeof(TbClaim));
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.WritesFrom((TbClaim c) => new TbClaimDto
            {
                Number = c.Number,
                SubmitUserId = c.CompleteUser.Id,
                CompleteUserId = c.SubmitUser.Id,
                Note = c.Note,
                Status = c.Status,
                Amount = c.Amount,
            }, lineage));

            Assert.Contains("TbClaim → TbClaimDto copies values from other members than expected", error.Message);
            Assert.Matches(@"CompleteUserId: expected the value of CompleteUser\.Id \(\d+\), but it holds \d+, the value of SubmitUser\.Id", error.Message);
            Assert.Matches(@"SubmitUserId: expected the value of SubmitUser\.Id \(\d+\), but it holds \d+, the value of CompleteUser\.Id", error.Message);
        }

        [Fact]
        public void A_value_left_empty_is_reported_and_inline_pairs_work()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.WritesFrom((TbClaim c) => new TbClaimDto { Number = c.Number },
                ("Number", "Number"), ("Note", "Note")));
            Assert.Contains("Note: expected the value of Note (\"Note-", error.Message);
            Assert.Contains("but it is null", error.Message);

            var unknown = Assert.Throws<MapperAssertException>(() => MapperAssert.WritesFrom((TbClaim c) => TbMappings.ToDto(c), ("Number", "Numbr")));
            Assert.Contains("TbClaim has no member path 'Numbr'", unknown.Message);
        }

        [Fact]
        public void LineageOf_reports_signature_changes()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.LineageOf(typeof(TbMappings), nameof(TbMappings.ToDto), typeof(int)));
            Assert.Contains("No generated overload of 'ToDto' has these parameter types", error.Message);
        }

        // ------------------------------------------------------------------ AllMappersMatchWriteSets

        [Fact]
        public void Every_generated_method_matches_its_write_set_and_lineage()
        {
            MapperAssert.AllMappersMatchWriteSets(typeof(TbMappings));
            MapperAssert.AllMappersMatchWriteSets(typeof(TpMappings));
        }

        [Fact]
        public void A_method_that_throws_on_sample_data_is_reported()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.AllMappersMatchWriteSets(typeof(TbBroken)));
            Assert.Contains("TbBroken.ToDto(TbCode): threw FormatException", error.Message);

            // With valid values for the member, it passes.
            MapperAssert.AllMappersMatchWriteSets(typeof(TbBroken), new SampleDataOptions().For<TbCode, string>(c => c.Code, n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void Utility_and_merge_methods_do_not_break_the_baseline()
        {
            // Diff and ToLogString write no destination: they have no write set.
            Assert.DoesNotContain(Utility.UMapper.WriteSets.Keys, k => k.StartsWith("Diff", StringComparison.Ordinal) || k.StartsWith("PublicDiff", StringComparison.Ordinal) || k.StartsWith("ToLogString", StringComparison.Ordinal));
            Assert.Contains(Utility.UMapper.WriteSets.Keys, k => k.StartsWith("Redact", StringComparison.Ordinal)); // a real copy
            MapperAssert.AllMappersMatchWriteSets(typeof(Utility.UMapper));
            MapperAssert.AllMappersMatchWriteSets(typeof(Merge.MMapper)); // merges are skipped, Update and ToCoverage run
        }

        // ------------------------------------------------------------------ RoundTrips

        [Fact]
        public void Map_and_reverse_map_round_trip()
        {
            MapperAssert.RoundTrips<TbPerson, TbPersonDto>(TbMappings.ToDto, TbMappings.ToPerson);
            MapperAssert.RoundTrips<TbPerson, TbPersonDto>(TbMappings.ToDto, TbMappings.ToPerson, runs: 50);
        }

        [Fact]
        public void A_member_lost_on_the_way_fails_the_round_trip()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.RoundTrips<TbPerson, TbPersonLite>(TbMappings.ToLite, TbMappings.FromLite));
            Assert.Contains("Round trip TbPerson → TbPersonLite → TbPerson changed values (default values):", error.Message);
            Assert.Contains("Nickname: \"Nickname-", error.Message);
            Assert.Contains("\" became null", error.Message);

            MapperAssert.RoundTrips<TbPerson, TbPersonLite>(TbMappings.ToLite, TbMappings.FromLite, ignore: new[] { "Nickname" });
        }

        [Fact]
        public void Randomized_runs_find_null_handling_bugs_and_name_the_seed()
        {
            // A hand-written reverse that assumes Nickname is never null.
            TbPerson Back(TbPersonDto d) => new TbPerson
            {
                Id = d.Id, Name = d.Name, Nickname = d.Nickname!.ToString(), Age = d.Age, Tags = d.Tags,
                Address = new TbAddress { Street = d.Address.Street, Zip = d.Address.Zip },
            };

            MapperAssert.RoundTrips<TbPerson, TbPersonDto>(TbMappings.ToDto, Back); // default values: never null
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.RoundTrips<TbPerson, TbPersonDto>(TbMappings.ToDto, Back, runs: 100));
            Assert.Matches(@"threw NullReferenceException \(seed \d+\)", error.Message);

            // The seed replays the failure.
            var seed = int.Parse(System.Text.RegularExpressions.Regex.Match(error.Message, @"seed (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Throws<MapperAssertException>(() => MapperAssert.RoundTrips<TbPerson, TbPersonDto>(TbMappings.ToDto, Back, options: new SampleDataOptions().Randomized(seed)));
        }

        // ------------------------------------------------------------------ AllMethodsUsed

        [Fact]
        public void Mapping_methods_no_production_code_uses_are_listed()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.AllMethodsUsed(typeof(TbUsage), typeof(TbService).Assembly));
            Assert.Contains("TbUsage.Unused(TbOrder)", error.Message);
            Assert.DoesNotContain("TbUsage.Used(", error.Message);
            Assert.DoesNotContain("NestedOnly", error.Message); // reached through Used's generated code
            Assert.Contains("candidates for deletion", error.Message);
        }

        // ------------------------------------------------------------------ SampleData options

        [Fact]
        public void Sample_data_takes_type_member_and_name_overrides()
        {
            var options = new SampleDataOptions()
                .For<TbAddress, string?>(a => a.Zip, n => (10000 + n).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .For<int?>(() => 42)
                .ForMember("Nickname", () => "Nick");
            var person = SampleData.Create<TbPerson>(options);

            Assert.Matches(@"^1\d{4}$", person.Address.Zip);
            Assert.Equal(42, person.Age);
            Assert.Equal("Nick", person.Nickname);
            Assert.StartsWith("Name-", person.Name);

            // A record's constructor parameter counts as its member.
            var record = SampleData.Create<TbZipRecord>(new SampleDataOptions().For<TbZipRecord, string>(r => r.Zip, () => "LS1 4AP"));
            Assert.Equal("LS1 4AP", record.Zip);
            Assert.StartsWith("City-", record.City);
        }

        [Fact]
        public void Sample_data_limits_enums_to_a_subset()
        {
            Assert.Equal(TbStatus.Submitted, SampleData.Create<TbClaim>().Status); // first non-zero by default
            Assert.Equal(TbStatus.Closed, SampleData.Create<TbClaim>(new SampleDataOptions().Enum(TbStatus.Closed)).Status);
            var seen = Enumerable.Range(0, 40)
                .Select(i => SampleData.Create<TbClaim>(new SampleDataOptions().Enum(TbStatus.Open, TbStatus.Closed).Randomized(i)).Status)
                .Distinct().OrderBy(s => s).ToList();
            Assert.Equal(new[] { TbStatus.Open, TbStatus.Closed }, seen);
        }

        [Fact]
        public void Sample_data_builds_types_without_a_public_parameterless_constructor()
        {
            // A private parameterless constructor (EF entities) is used automatically.
            var guarded = SampleData.Create<TbGuarded>();
            Assert.NotNull(guarded);
            Assert.NotEqual(0, guarded.Count);
            Assert.Equal("", guarded.Name); // private setter left alone...
            Assert.StartsWith("Name-", SampleData.Create<TbGuarded>(new SampleDataOptions { FillNonPublicSetters = true }).Name); // ...unless asked

            // Constructor parameters are filled.
            var noDefault = SampleData.Create<TbNoDefault>();
            Assert.NotEqual(Guid.Empty, noDefault.Key);
            Assert.StartsWith("label-", noDefault.Label);

            // An interface member needs a factory; its other members are then filled.
            Assert.Null(SampleData.Create<TbHasShape>().Shape);
            var shape = SampleData.Create<TbHasShape>(new SampleDataOptions().Construct<ITbShape>(() => new TbSquare())).Shape;
            var square = Assert.IsType<TbSquare>(shape);
            Assert.StartsWith("Label-", square.Label);
            Assert.NotEqual(0, square.Side);
        }

        [Fact]
        public void Sample_data_collection_size_and_depth_are_configurable()
        {
            var person = SampleData.Create<TbPerson>(new SampleDataOptions { CollectionSize = 3 });
            Assert.Equal(3, person.Tags.Count);
            Assert.Equal(3, person.Tags.Distinct().Count());
            var shallow = SampleData.Create<TbClaim>(new SampleDataOptions { MaxDepth = 1 });
            Assert.NotEqual(0, shallow.Number);
            Assert.Null(shallow.SubmitUser); // nested objects beyond the depth stay default
        }

        [Fact]
        public void Randomized_sample_data_is_repeatable_and_covers_edge_cases()
        {
            string Describe(TbPerson p) => string.Join("|", p.Id, p.Name, p.Nickname ?? "<null>", p.Age?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>", p.Tags.Count);
            Assert.Equal(Describe(SampleData.Create<TbPerson>(new SampleDataOptions().Randomized(7))), Describe(SampleData.Create<TbPerson>(new SampleDataOptions().Randomized(7))));

            var people = Enumerable.Range(0, 200).Select(i => SampleData.Create<TbPerson>(new SampleDataOptions().Randomized(i))).ToList();
            Assert.Contains(people, p => p.Nickname == null);        // string? can be null
            Assert.DoesNotContain(people, p => p.Name == null);      // string cannot
            Assert.Contains(people, p => p.Age == null);
            Assert.Contains(people, p => p.Name.Length == 0);
            Assert.Contains(people, p => p.Name.Length > 300);
            Assert.Contains(people, p => p.Id == int.MaxValue || p.Id == int.MinValue || p.Id == 0);
            Assert.Contains(people, p => p.Tags.Count == 0);
            Assert.Contains(people, p => p.Tags.Count > 1);
        }

        [Fact]
        public void Checks_that_need_distinct_values_refuse_random_mode()
        {
            Assert.Throws<ArgumentException>(() => MapperAssert.WritesFrom((TbClaim c) => TbMappings.ToDto(c),
                new Dictionary<string, string>(), new SampleDataOptions().Randomized(1)));
            Assert.Throws<ArgumentException>(() => MapperAssert.AllMappersMatchWriteSets(typeof(TbMappings), new SampleDataOptions().Randomized(1)));
        }
    }
}
