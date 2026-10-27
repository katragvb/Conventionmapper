using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ConventionMapper.Testing;
using ConventionMapper.Tests.TestingPackage;

namespace ConventionMapper.Tests
{
    public class BuilderTests
    {
        public sealed class Coordinates { public double Lat { get; set; } public double Lng { get; set; } }
        public struct Money { public decimal Amount { get; set; } public string Currency { get; set; } }

        public sealed class Shop
        {
            private Shop() { }
            public int Id { get; private set; }
            public string Name { get; init; } = "";
            public TbAddress? Address { get; set; }
            public Money Budget { get; set; }
            public string Code { get; } = "fixed";
            public Coordinates? Location { get; set; }
        }

        [Fact]
        public void Builder_starts_filled_and_sets_named_members()
        {
            var person = SampleData.Builder<TbPerson>().With(p => p.Name, "Ann").With(p => p.Address.Zip, "LS1").Without(p => p.Nickname).Build();
            Assert.Equal("Ann", person.Name);
            Assert.Equal("LS1", person.Address.Zip);
            Assert.Null(person.Nickname);
            Assert.StartsWith("Street-", person.Address.Street); // the rest stays filled
            Assert.NotEqual(0, person.Id);
        }

        [Fact]
        public void Builder_handles_guarded_members_nested_paths_and_structs()
        {
            var shop = SampleData.Builder<Shop>().Blank()
                .With(s => s.Id, 7)                 // private setter
                .With(s => s.Name, "Main")          // init
                .With(s => s.Code, "C1")            // get-only auto-property
                .With(s => s.Address!.Street, "Leeds") // created on the way
                .With(s => s.Budget.Amount, 10m)    // inside a struct
                .With(s => s.Location!.Lat, 53.8)
                .Build();
            Assert.Equal((7, "Main", "C1", "Leeds", 10m, 53.8), (shop.Id, shop.Name, shop.Code, shop.Address!.Street, shop.Budget.Amount, shop.Location!.Lat));
            Assert.Null(shop.Budget.Currency); // blank start
        }

        [Fact]
        public void BuildMany_gives_distinct_values_and_indexes()
        {
            var people = SampleData.Builder<TbPerson>().With(p => p.Id, i => 1000 + i).Configure(p => p.Tags.Clear()).BuildMany(3);
            Assert.Equal(new[] { 1000, 1001, 1002 }, people.Select(p => p.Id));
            Assert.Equal(3, people.Select(p => p.Name).Distinct().Count());
            Assert.All(people, p => Assert.Empty(p.Tags));

            var options = new SampleDataOptions().ForMember("Name", () => "Fixed");
            Assert.All(SampleData.Builder<TbPerson>(options).BuildMany(2), p => Assert.Equal("Fixed", p.Name));
        }

        [Fact]
        public void Builder_rejects_expressions_that_are_not_member_paths()
        {
            Assert.Throws<ArgumentException>(() => SampleData.Builder<TbPerson>().With(p => p.Name.Trim(), "x"));
        }
    }

    [Collection(SnapshotCollection.Name)]
    public sealed class GeneratedCodeSnapshotTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-code-" + Guid.NewGuid().ToString("N"));

        public GeneratedCodeSnapshotTests() => Environment.SetEnvironmentVariable(MapperAssertUpdate, null);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(MapperAssertUpdate, null);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        private const string MapperAssertUpdate = "CONVENTIONMAPPER_UPDATE_SNAPSHOTS";

        [Fact]
        public void Generated_code_is_embedded_per_method()
        {
            var code = (string)typeof(TbMappings).GetMethod("GeneratedCodeOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { nameof(TbMappings.ToLite), Type.EmptyTypes })!;
            Assert.StartsWith("public static partial global::ConventionMapper.Tests.TestingPackage.TbPersonLite ToLite(global::ConventionMapper.Tests.TestingPackage.TbPerson source)\n{", code);
            Assert.DoesNotContain("#cm-", code);
        }

        [Fact]
        public void One_method_is_snapshotted_in_its_own_file()
        {
            var file = Path.Combine(_folder, "TbMappings.ToDto(TbClaim).cs");
            var created = Assert.Throws<MapperAssertException>(() => MapperAssert.GeneratedCodeMatches(typeof(TbMappings), nameof(TbMappings.ToDto), new[] { typeof(TbClaim) }, _folder));
            Assert.Contains("Created generated code of TbMappings.ToDto(TbClaim) snapshot", created.Message);
            Assert.Contains("SubmitUserId = ", File.ReadAllText(file));
            MapperAssert.GeneratedCodeMatches(typeof(TbMappings), nameof(TbMappings.ToDto), new[] { typeof(TbClaim) }, _folder);

            File.WriteAllText(file, File.ReadAllText(file).Replace("SubmitUserId = ", "SubmitterId = "));
            var changed = Assert.Throws<MapperAssertException>(() => MapperAssert.GeneratedCodeMatches(typeof(TbMappings), nameof(TbMappings.ToDto), new[] { typeof(TbClaim) }, _folder));
            Assert.Contains("Generated code of TbMappings.ToDto(TbClaim) changed:", changed.Message);
            Assert.Contains("+ ", changed.Message);
            Assert.Contains("SubmitterId", changed.Message);

            Environment.SetEnvironmentVariable(MapperAssertUpdate, "1");
            MapperAssert.GeneratedCodeMatches(typeof(TbMappings), nameof(TbMappings.ToDto), new[] { typeof(TbClaim) }, _folder);
            Assert.DoesNotContain("SubmitterId", File.ReadAllText(file));
        }

        [Fact]
        public void Directory_mode_covers_every_method_and_flags_stale_files()
        {
            Assert.Throws<MapperAssertException>(() => MapperAssert.GeneratedCodeMatchesDirectory(typeof(TbMappings), _folder));
            var files = Directory.GetFiles(Path.Combine(_folder, "TbMappings")).Select(Path.GetFileName).ToList();
            Assert.Contains("ToDto(TbClaim,string_).cs", files);   // "?" is not allowed in file names
            Assert.Contains("ToLite(TbPerson).cs", files);
            MapperAssert.GeneratedCodeMatchesDirectory(typeof(TbMappings), _folder);

            File.WriteAllText(Path.Combine(_folder, "TbMappings", "Removed(Thing).cs"), "old");
            var stale = Assert.Throws<MapperAssertException>(() => MapperAssert.GeneratedCodeMatchesDirectory(typeof(TbMappings), _folder));
            Assert.Contains("Stale generated-code snapshot", stale.Message);
        }

        [Fact]
        public void Unknown_methods_list_the_overloads()
        {
            var error = Assert.Throws<MapperAssertException>(() => MapperAssert.GeneratedCodeMatches(typeof(TbMappings), nameof(TbMappings.ToDto), new[] { typeof(int) }, _folder));
            Assert.Contains("No generated overload of 'ToDto'", error.Message);
        }
    }
}
