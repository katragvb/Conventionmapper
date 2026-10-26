using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests.Merge
{
    public class MCoverageInput { public Guid ContextId { get; set; } public string Name { get; set; } = ""; public decimal Limit { get; set; } }

    public class MCoverage
    {
        public int Id { get; set; }
        public Guid ContextId { get; set; }
        public string Name { get; set; } = "";
        public decimal Limit { get; set; }
        public string Audit { get; set; } = "";
    }

    [GeneratedMapper]
    public static partial class MMapper
    {
        // Matched items: updated with this method and its configuration (Id and Audit are left alone).
        [MapIgnore(nameof(MCoverage.Id)), MapIgnore(nameof(MCoverage.Audit))]
        public static partial void Update(MCoverageInput source, MCoverage target);

        [MapIgnore(nameof(MCoverage.Id)), MapIgnore(nameof(MCoverage.Audit))]
        public static partial MCoverage ToCoverage(MCoverageInput source);

        [MapMergeKey(nameof(MCoverageInput.ContextId))]
        public static partial void Merge(IEnumerable<MCoverageInput> source, ICollection<MCoverage> target);

        [MapMergeKey(nameof(MCoverageInput.ContextId), RemoveMissing = false)]
        public static partial void MergeKeep(IEnumerable<MCoverageInput> source, ICollection<MCoverage> target);
    }

    public class GeneratedMergeTests
    {
        private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

        private static List<MCoverage> Existing() => new List<MCoverage>
        {
            new MCoverage { Id = 1, ContextId = A, Name = "a", Limit = 1, Audit = "kept" },
            new MCoverage { Id = 2, ContextId = B, Name = "b", Limit = 2, Audit = "kept" },
        };

        private static MCoverageInput[] Input() => new[]
        {
            new MCoverageInput { ContextId = A, Name = "a2", Limit = 10 }, // update
            new MCoverageInput { ContextId = C, Name = "c", Limit = 30 },  // add
        };                                                                  // B: remove

        [Fact]
        public void Merge_updates_matched_adds_new_and_removes_missing()
        {
            var target = Existing();
            var first = target[0];

            MMapper.Merge(Input(), target);

            Assert.Equal(2, target.Count);
            Assert.Same(first, target[0]);                 // updated in place
            Assert.Equal("a2", first.Name);
            Assert.Equal(10, first.Limit);
            Assert.Equal(1, first.Id);                     // Update's [MapIgnore] applies
            Assert.Equal("kept", first.Audit);
            Assert.DoesNotContain(target, c => c.ContextId == B);
            var added = Assert.Single(target, c => c.ContextId == C);
            Assert.Equal("c", added.Name);
        }

        [Fact]
        public void Merge_can_keep_missing_items()
        {
            var target = Existing();

            MMapper.MergeKeep(Input(), target);

            Assert.Equal(3, target.Count);
            Assert.Contains(target, c => c.ContextId == B);
        }

        [Fact]
        public void Merge_with_null_source_changes_nothing()
        {
            var target = Existing();
            MMapper.Merge(null!, target);
            Assert.Equal(2, target.Count);
        }
    }
}
