using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

// End-to-end: [MapPatch], [MapCombine] / [MapPrefer] and [MapWhen], compiled by the generator in this project and run.
namespace ConventionMapper.Tests.Kinds
{
    /// <summary>An Optional&lt;T&gt;-shaped wrapper, as PATCH APIs use to tell "not sent" from "sent as null".</summary>
    public readonly struct Optional<T>
    {
        public Optional(T value) { Value = value; HasValue = true; }
        public bool HasValue { get; }
        public T Value { get; }
        public static implicit operator Optional<T>(T value) => new Optional<T>(value);
    }

    public class Customer
    {
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public int Age { get; set; }
        public int Visits { get; set; }
        public string? Note { get; set; }
        public decimal Discount { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class CustomerPatch
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public int? Age { get; set; }
        public int Visits { get; set; }               // not nullable: always copied
        public Optional<string?> Note { get; set; }   // "sent as null" clears it, "not sent" keeps it
        public List<string>? Tags { get; set; }
    }

    public class CustomerInput
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public decimal Discount { get; set; }
        public bool Loyal { get; set; }
    }

    public class Address
    {
        public string? Street { get; set; }
        public string? City { get; set; }
        public int Floor { get; set; }
        public int? Units { get; set; }
        public List<string>? Lines { get; set; }
    }

    [GeneratedMapper]
    public static partial class KindsMapper
    {
        [MapPatch]
        public static partial void Patch(CustomerPatch patch, Customer target);

        [MapCombine]
        [MapPrefer("saved", nameof(Address.City))]
        public static partial Address Combine(Address entered, Address saved, Address defaults);

        [MapWhen(nameof(Customer.Discount), nameof(IsEligible))]
        [MapIgnore(nameof(Customer.Email)), MapIgnore(nameof(Customer.Note)), MapIgnore(nameof(Customer.Tags)), MapIgnore(nameof(Customer.Visits))]
        public static partial Customer ToCustomer(CustomerInput input);

        [MapWhen(nameof(Customer.Discount), nameof(IsEligible))]
        [MapIgnore(nameof(Customer.Email)), MapIgnore(nameof(Customer.Note)), MapIgnore(nameof(Customer.Tags)), MapIgnore(nameof(Customer.Visits))]
        public static partial void Update(CustomerInput input, Customer target);

        [MapWhen(nameof(Customer.Discount), nameof(IsEligible))]
        [MapIgnore(nameof(Customer.Email)), MapIgnore(nameof(Customer.Note)), MapIgnore(nameof(Customer.Tags)), MapIgnore(nameof(Customer.Visits))]
        public static partial IQueryable<Customer> Project(IQueryable<CustomerInput> query);

        private static bool IsEligible(CustomerInput input) => input.Loyal && input.Age >= 18;
    }

    public class GeneratedKindsTests
    {
        [Fact]
        public void Patch_copies_only_what_the_caller_set()
        {
            var target = new Customer { Name = "Ann", Email = "ann@example.com", Age = 40, Visits = 3, Note = "vip", Tags = new List<string> { "a" } };

            KindsMapper.Patch(new CustomerPatch { Age = 41, Visits = 4 }, target);
            Assert.Equal(("Ann", "ann@example.com", 41, 4, "vip"), (target.Name, target.Email, target.Age, target.Visits, target.Note));
            Assert.Equal(new[] { "a" }, target.Tags);

            KindsMapper.Patch(new CustomerPatch { Email = "new@example.com", Note = new Optional<string?>(null), Tags = new List<string>() }, target);
            Assert.Equal("new@example.com", target.Email);
            Assert.Null(target.Note);         // sent as null
            Assert.Empty(target.Tags);
            Assert.Equal(41, target.Age);     // not sent
            Assert.Equal(0, target.Visits);   // non-nullable: always copied
        }

        [Fact]
        public void Combine_takes_the_first_source_with_a_value()
        {
            var entered = new Address { Street = "1 High St", Floor = 0, City = "Typed" };
            var saved = new Address { Street = "9 Old Rd", City = "Leeds", Floor = 3, Units = 12, Lines = new List<string> { "x" } };
            var defaults = new Address { City = "London", Floor = 1, Units = 1 };

            var combined = KindsMapper.Combine(entered, saved, defaults);

            Assert.Equal("1 High St", combined.Street);   // entered wins
            Assert.Equal("Leeds", combined.City);         // [MapPrefer("saved", "City")]
            Assert.Equal(3, combined.Floor);              // entered's 0 is the default, so saved's
            Assert.Equal(12, combined.Units);
            Assert.Equal(new[] { "x" }, combined.Lines);
            Assert.Same(saved.Lines, combined.Lines);     // same type: assigned as it is, like every map

            var fromDefaults = KindsMapper.Combine(null!, null!, defaults);
            Assert.Equal(("London", 1, 1), (fromDefaults.City, fromDefaults.Floor, fromDefaults.Units));
        }

        [Fact]
        public void When_writes_a_member_only_when_the_predicate_holds()
        {
            var loyal = new CustomerInput { Name = "Ann", Age = 30, Discount = 0.1m, Loyal = true };
            var minor = new CustomerInput { Name = "Bo", Age = 16, Discount = 0.1m, Loyal = true };

            Assert.Equal(0.1m, KindsMapper.ToCustomer(loyal).Discount);
            Assert.Equal(0m, KindsMapper.ToCustomer(minor).Discount);

            var target = new Customer { Discount = 0.5m };
            KindsMapper.Update(minor, target);
            Assert.Equal(0.5m, target.Discount); // kept
            Assert.Equal("Bo", target.Name);
            KindsMapper.Update(loyal, target);
            Assert.Equal(0.1m, target.Discount);

            var projected = KindsMapper.Project(new[] { loyal, minor }.AsQueryable()).ToList();
            Assert.Equal(new[] { 0.1m, 0m }, projected.Select(c => c.Discount));
        }

        [Fact]
        public void Conditions_and_patches_appear_in_the_report()
        {
            var report = ConventionMapper.Generated.DataFlowReport.ByMapper["ConventionMapper.Tests.Kinds.KindsMapper"];
            Assert.Contains("| Discount | `Discount` | when `IsEligible()` |", report);
            Assert.Contains("| Email | `Email` | only when set |", report);
            Assert.Contains("| City | `saved.City`, then `entered.City`, then `defaults.City` | first with a value |", report);
            Assert.Contains("| Discount | — | not in the patch |", report);
        }
    }
}
