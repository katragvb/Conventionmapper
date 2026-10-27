using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests.Utility
{
    public class UAddress { public string City { get; set; } = ""; [Sensitive] public string Street { get; set; } = ""; }

    public class UPerson
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        [Sensitive] public string Ssn { get; set; } = "";
        [Sensitive] public DateTime? DateOfBirth { get; set; }
        public UAddress? Home { get; set; }
        public List<UAddress> Previous { get; set; } = new List<UAddress>();
        public decimal Score { get; set; }
    }

    public class UPolicy { public int Id { get; set; } public string Status { get; set; } = ""; public decimal Premium { get; set; } public List<string> Tags { get; set; } = new List<string>(); [Sensitive] public string TaxId { get; set; } = ""; }
    public class UEndorsement { public int Id { get; set; } public string Status { get; set; } = ""; public decimal Premium { get; set; } public List<string> Tags { get; set; } = new List<string>(); [Sensitive] public string TaxId { get; set; } = ""; public string Note { get; set; } = ""; }

    [GeneratedMapper]
    public static partial class UMapper
    {
        [MapRedact] public static partial UPerson Redact(UPerson person);

        [MapLogString] public static partial string ToLogString(UPerson person);

        // FieldChange is internal (generated into each assembly), so the method is internal too...
        internal static partial IReadOnlyList<FieldChange> Diff(UPolicy before, UEndorsement after);

        // ...and a public method returns tuples.
        public static partial IReadOnlyList<(string Member, object? Before, object? After)> PublicDiff(UPolicy before, UEndorsement after);
    }

    public class GeneratedUtilityTests
    {
        private static UPerson Person() => new UPerson
        {
            Id = 7, Name = "Ada", Ssn = "123-45-6789", DateOfBirth = new DateTime(1990, 1, 2), Score = 1.5m,
            Home = new UAddress { City = "Cincinnati", Street = "1 Main St" },
            Previous = { new UAddress { City = "Dayton", Street = "2 Oak St" } },
        };

        [Fact]
        public void Redact_masks_sensitive_members_deeply_and_leaves_the_original_alone()
        {
            var person = Person();

            var copy = UMapper.Redact(person);

            Assert.NotSame(person, copy);
            Assert.Equal("***", copy.Ssn);
            Assert.Null(copy.DateOfBirth);
            Assert.Equal("Ada", copy.Name);
            Assert.Equal("Cincinnati", copy.Home!.City);
            Assert.Equal("***", copy.Home.Street);                 // nested object
            Assert.Equal("***", copy.Previous.Single().Street);   // collection
            Assert.Equal("123-45-6789", person.Ssn);              // original untouched
            Assert.Equal("1 Main St", person.Home!.Street);
        }

        [Fact]
        public void Log_string_shows_members_and_masks_sensitive_ones()
        {
            var text = UMapper.ToLogString(Person());

            Assert.Equal("UPerson { Id = 7, Name = \"Ada\", Ssn = ***, DateOfBirth = ***, Home = UAddress { City = \"Cincinnati\", Street = *** }, Previous = [1 items], Score = 1.5 }", text);
            Assert.Equal("null", UMapper.ToLogString(null!));
        }

        [Fact]
        public void Diff_lists_changed_members_and_masks_sensitive_values()
        {
            var policy = new UPolicy { Id = 1, Status = "Bound", Premium = 100, Tags = { "a" }, TaxId = "11-111" };
            var endorsement = new UEndorsement { Id = 1, Status = "Endorsed", Premium = 100, Tags = { "a" }, TaxId = "22-222", Note = "only on one side" };

            var changes = UMapper.Diff(policy, endorsement);

            Assert.Equal(new[] { "Status", "TaxId" }, changes.Select(c => c.Member).OrderBy(m => m));
            var status = changes.Single(c => c.Member == "Status");
            Assert.Equal("Bound", status.Before);
            Assert.Equal("Endorsed", status.After);
            Assert.Equal("Status: Bound -> Endorsed", status.ToString());
            Assert.Equal("***", changes.Single(c => c.Member == "TaxId").After);

            endorsement.Tags.Add("b");
            Assert.Contains(UMapper.Diff(policy, endorsement), c => c.Member == "Tags"); // collections by content
            Assert.Empty(UMapper.Diff(policy, new UEndorsement { Id = 1, Status = "Bound", Premium = 100, Tags = { "a" }, TaxId = "11-111" }));
        }

        [Fact]
        public void A_public_diff_returns_tuples()
        {
            var changes = UMapper.PublicDiff(new UPolicy { Status = "Bound" }, new UEndorsement { Status = "Endorsed" });

            var change = Assert.Single(changes);
            Assert.Equal(("Status", (object?)"Bound", (object?)"Endorsed"), change);
        }
    }
}
