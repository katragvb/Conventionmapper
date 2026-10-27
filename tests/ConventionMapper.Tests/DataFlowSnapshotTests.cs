using System;
using System.IO;
using System.Linq;
using ConventionMapper.Generated;

namespace ConventionMapper.Tests
{
    /// <summary>Tests that change CONVENTIONMAPPER_UPDATE_SNAPSHOTS run one after another.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SnapshotCollection
    {
        public const string Name = "Snapshots";
    }

    /// <summary>
    /// The snapshot helpers the generator writes into this assembly (DataFlowReport.AssertMatches / AssertMatchesDirectory).
    /// Kept in one class: the tests change the CONVENTIONMAPPER_UPDATE_SNAPSHOTS environment variable.
    /// </summary>
    [Collection(SnapshotCollection.Name)]
    public class DataFlowSnapshotTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-snapshots-" + Guid.NewGuid().ToString("N"));

        public DataFlowSnapshotTests()
        {
            Environment.SetEnvironmentVariable(DataFlowReport.UpdateVariable, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(DataFlowReport.UpdateVariable, null);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Fact]
        public void Missing_file_is_created_and_fails_once()
        {
            var file = Path.Combine(_folder, "data-flow.md");

            var created = Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatches(file));
            Assert.Contains("Created data-flow report", created.Message);
            Assert.StartsWith("# Data-flow report", File.ReadAllText(file));

            DataFlowReport.AssertMatches(file); // now it matches
        }

        [Fact]
        public void Line_endings_and_trailing_spaces_are_ignored()
        {
            var file = Path.Combine(_folder, "data-flow.md");
            Directory.CreateDirectory(_folder);
            File.WriteAllText(file, DataFlowReport.Markdown.Replace("\n", "  \r\n") + "\r\n\r\n");

            DataFlowReport.AssertMatches(file);
        }

        [Fact]
        public void A_change_fails_with_a_line_diff()
        {
            var file = Path.Combine(_folder, "data-flow.md");
            Directory.CreateDirectory(_folder);
            File.WriteAllText(file, DataFlowReport.Markdown.Replace("| AddressCity | `Address.City` | flattened |", "| AddressCity | `Address.Town` | flattened |"));

            var error = Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatches(file));

            Assert.Contains("Data-flow report changed: " + file, error.Message);
            Assert.Contains("- | AddressCity | `Address.Town` | flattened |", error.Message);
            Assert.Contains("+ | AddressCity | `Address.City` | flattened |", error.Message);
            Assert.Contains("CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1", error.Message);
            Assert.Contains("Address.Town", File.ReadAllText(file)); // not overwritten without the update switch
        }

        [Fact]
        public void A_change_is_also_described_in_plain_English()
        {
            var file = Path.Combine(_folder, "data-flow.md");
            Directory.CreateDirectory(_folder);
            var old = DataFlowReport.Markdown
                .Replace("| AddressCity | `Address.City` | flattened |", "| AddressCity | `Address.Town` | flattened |")
                .Replace("| Name | `Name` |  |", "| Name | — | ignored |")
                .Replace("| Label | — | ignored |\n", "| Label | — | ignored |\n| Legacy | `Legacy` |  |\n");
            File.WriteAllText(file, old);

            var error = Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatches(file));

            Assert.Contains("Changes:", error.Message);
            Assert.Contains("- ToDto(FlowCustomer) now writes AddressCity from Address.City instead of Address.Town", error.Message);
            Assert.Contains("- ToDto(FlowCustomer) now also writes Name (from Name)", error.Message);
            Assert.Contains("- ToDto(FlowCustomer) no longer writes Legacy (was Legacy)", error.Message);
            Assert.Contains("- FlowCustomerDto lost the member Legacy (API change)", error.Message);
        }

        [Fact]
        public void Report_has_csv_json_and_mermaid_exports()
        {
            Assert.StartsWith("Mapper,Method,SourceType,SourceMember,DestinationType,DestinationMember,Notes\n", DataFlowReport.Csv);
            Assert.StartsWith("[{\"mapper\":", DataFlowReport.Json);
            Assert.StartsWith("flowchart LR\n", DataFlowReport.Mermaid);
        }

        [Fact]
        public void Update_switch_rewrites_the_file()
        {
            var file = Path.Combine(_folder, "data-flow.md");
            Directory.CreateDirectory(_folder);
            File.WriteAllText(file, "old");
            Environment.SetEnvironmentVariable(DataFlowReport.UpdateVariable, "1");

            DataFlowReport.AssertMatches(file);

            Assert.Equal(DataFlowReport.Markdown.TrimEnd('\n') + "\n", File.ReadAllText(file));
        }

        [Fact]
        public void Directory_mode_writes_one_file_per_mapper_and_reports_stale_files()
        {
            Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatchesDirectory(_folder));
            var files = Directory.GetFiles(_folder, "*.md").Select(Path.GetFileName).ToList();
            Assert.Equal(DataFlowReport.ByMapper.Count, files.Count);
            Assert.Contains("ConventionMapper.Tests.FlowMapper.md", files);
            Assert.Contains("## `ConventionMapper.Tests.FlowMapper`", File.ReadAllText(Path.Combine(_folder, "ConventionMapper.Tests.FlowMapper.md")));
            DataFlowReport.AssertMatchesDirectory(_folder);

            File.WriteAllText(Path.Combine(_folder, "Removed.Mapper.md"), "# old");
            var stale = Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatchesDirectory(_folder));
            Assert.Contains("Stale data-flow report", stale.Message);

            Environment.SetEnvironmentVariable(DataFlowReport.UpdateVariable, "true");
            DataFlowReport.AssertMatchesDirectory(_folder);
            Assert.False(File.Exists(Path.Combine(_folder, "Removed.Mapper.md")));
        }

        [Fact]
        public void Relative_paths_start_at_the_calling_source_file()
        {
            var relative = Path.Combine("obj", "snapshot-test-" + Guid.NewGuid().ToString("N"), "flow.md");
            var expected = Path.Combine(SourceFolder(), relative);
            try
            {
                Assert.Throws<DataFlowReport.SnapshotMismatchException>(() => DataFlowReport.AssertMatches(relative));
                Assert.True(File.Exists(expected), expected);
            }
            finally
            {
                if (Directory.Exists(Path.GetDirectoryName(expected))) Directory.Delete(Path.GetDirectoryName(expected)!, true);
            }
        }

        private static string SourceFolder([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    }
}
