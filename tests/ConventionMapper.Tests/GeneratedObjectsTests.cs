using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ConventionMapper;
#if NET8_0_OR_GREATER
using Microsoft.Data.Sqlite;
#endif

// End-to-end: reading rows, [MapClone], [MapEquality] and [MapPreserveReferences], compiled by the generator in this project and run.
namespace ConventionMapper.Tests.Objects
{
    public enum Tier { None, Silver, Gold }

    public class Account
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public decimal Balance { get; set; }
        public Tier Tier { get; set; }
        public DateTime? ClosedOn { get; set; }
        public Guid Key { get; set; }
    }

    public record AccountSummary(long Id, string Name, int Count);

    public class Node
    {
        public string Name { get; set; } = "";
        public Node? Parent { get; set; }
        public List<Node> Children { get; set; } = new List<Node>();
    }

    public class NodeDto
    {
        public string Name { get; set; } = "";
        public NodeDto? Parent { get; set; }
        public List<NodeDto> Children { get; set; } = new List<NodeDto>();
    }

    public class Order
    {
        public int Id { get; set; }
        public Address Ship { get; set; } = new Address();
        public List<Line> Lines { get; set; } = new List<Line>();
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();
        public HashSet<string> Tags { get; set; } = new HashSet<string>();
        public DateTime UpdatedAt { get; set; }
    }

    public class Address { public string City { get; set; } = ""; }
    public class Line { public string Sku { get; set; } = ""; public int Qty { get; set; } }

    [GeneratedMapper]
    public static partial class ObjectsMapper
    {
        public static partial Account Read(IDataRecord row);

        [MapMember("account_name", nameof(Account.Name))]
        public static partial List<Account> ReadAll(IDataReader reader);

        public static partial IEnumerable<AccountSummary> ReadSummaries(IDataReader reader);

        [MapClone]
        public static partial Order Clone(Order order);

        [MapEquality]
        [MapIgnore(nameof(Order.UpdatedAt))]
        public static partial IEqualityComparer<Order> OrderComparer();

        [MapPreserveReferences]
        public static partial NodeDto ToDto(Node node);
    }

    public class GeneratedObjectsTests
    {
#if NET8_0_OR_GREATER
        private static SqliteConnection Database()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = @"CREATE TABLE Accounts (Id INTEGER, account_name TEXT, Email TEXT, Balance TEXT, Tier INTEGER, ClosedOn TEXT, Key TEXT);
INSERT INTO Accounts VALUES (1, 'Ann', 'ann@example.com', '10.50', 2, NULL, '3f2504e0-4f89-11d3-9a0c-0305e82c3301');
INSERT INTO Accounts VALUES (2, 'Bo', NULL, '0', 1, '2024-05-01 00:00:00', '3f2504e0-4f89-11d3-9a0c-0305e82c3302');";
            create.ExecuteNonQuery();
            return connection;
        }

        [Fact]
        public void Rows_are_read_by_column_name_with_conversions_and_nulls()
        {
            using var db = Database();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Id, account_name, Email, Balance, Tier, ClosedOn, Key FROM Accounts ORDER BY Id";
            using var reader = command.ExecuteReader();

            var accounts = ObjectsMapper.ReadAll(reader);

            Assert.Equal(2, accounts.Count);
            Assert.Equal((1L, "Ann", "ann@example.com", 10.50m, Tier.Gold, (DateTime?)null), (accounts[0].Id, accounts[0].Name, accounts[0].Email, accounts[0].Balance, accounts[0].Tier, accounts[0].ClosedOn));
            Assert.Null(accounts[1].Email);
            Assert.Equal(new DateTime(2024, 5, 1), accounts[1].ClosedOn);
            Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3302"), accounts[1].Key);
        }

        [Fact]
        public void A_single_row_and_records_can_be_read()
        {
            using var db = Database();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Id, account_name AS Name, Email, Balance, Tier, ClosedOn, Key FROM Accounts WHERE Id = 1";
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("Ann", ObjectsMapper.Read(reader).Name);
            }

            command.CommandText = "SELECT Id, account_name AS Name, COUNT(*) AS Count FROM Accounts GROUP BY Id ORDER BY Id";
            using var summaries = command.ExecuteReader();
            Assert.Equal(new[] { new AccountSummary(1, "Ann", 1), new AccountSummary(2, "Bo", 1) }, ObjectsMapper.ReadSummaries(summaries).ToList());
        }

        [Fact]
        public void A_missing_column_names_the_member_and_the_columns()
        {
            using var db = Database();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Id, account_name FROM Accounts";
            using var reader = command.ExecuteReader();

            var error = Assert.Throws<InvalidOperationException>(() => ObjectsMapper.ReadAll(reader));
            Assert.Equal("Account.Email: the result has no column 'Email'. Columns: Id, account_name.", error.Message);
        }
#endif

        private static Order Sample() => new Order
        {
            Id = 1,
            Ship = new Address { City = "Leeds" },
            Lines = new List<Line> { new Line { Sku = "A", Qty = 2 } },
            Counts = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 },
            Tags = new HashSet<string> { "a", "b" },
            UpdatedAt = new DateTime(2024, 1, 1),
        };

        [Fact]
        public void Clone_copies_nested_objects_and_collections()
        {
            var original = Sample();
            var copy = ObjectsMapper.Clone(original);

            Assert.NotSame(original.Ship, copy.Ship);
            Assert.NotSame(original.Lines, copy.Lines);
            Assert.NotSame(original.Lines[0], copy.Lines[0]);
            Assert.NotSame(original.Counts, copy.Counts);
            Assert.NotSame(original.Tags, copy.Tags);
            Assert.True(ObjectsMapper.OrderComparer().Equals(original, copy));

            copy.Ship.City = "York";
            copy.Lines[0].Qty = 9;
            Assert.Equal("Leeds", original.Ship.City);
            Assert.Equal(2, original.Lines[0].Qty);
        }

        [Fact]
        public void Equality_compares_by_value_and_ignores_listed_members()
        {
            var comparer = ObjectsMapper.OrderComparer();
            Assert.Same(comparer, ObjectsMapper.OrderComparer());
            var a = Sample();
            var b = Sample();
            b.UpdatedAt = DateTime.Now;                 // [MapIgnore]
            b.Counts = new Dictionary<string, int> { ["y"] = 2, ["x"] = 1 }; // order doesn't matter
            b.Tags = new HashSet<string> { "b", "a" };

            Assert.True(comparer.Equals(a, b));
            Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));

            b.Lines[0].Qty = 3;
            Assert.False(comparer.Equals(a, b));
            b = Sample();
            b.Ship.City = "York";
            Assert.False(comparer.Equals(a, b));
            Assert.False(comparer.Equals(a, null));
            Assert.True(comparer.Equals(null, null));
        }

        [Fact]
        public void Shared_references_and_cycles_are_kept()
        {
            var root = new Node { Name = "root" };
            var child = new Node { Name = "child", Parent = root };
            root.Children.Add(child);
            root.Children.Add(child); // the same instance twice

            var dto = ObjectsMapper.ToDto(root);

            Assert.Equal(2, dto.Children.Count);
            Assert.Same(dto.Children[0], dto.Children[1]);
            Assert.Same(dto, dto.Children[0].Parent);    // the cycle ends at the already-mapped root
        }
    }
}
