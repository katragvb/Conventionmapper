using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ConventionMapper;

// End-to-end tests: the mappers below are implemented by ConventionMapper.SourceGenerator when this project builds.
// If the generator emitted invalid code, the test project would not compile.
namespace ConventionMapper.Tests.Generated
{
    public enum GenStatus { Pending, Shipped, Cancelled }
    public enum GenStatusDto { Unknown, Pending, Shipped, Cancelled }

    public class GenAddress { public string City { get; set; } = ""; }
    public class GenCustomer { public string Name { get; set; } = ""; public GenAddress? Address { get; set; } }
    public class GenLine { public string Sku { get; set; } = ""; public int Quantity { get; set; } public decimal Price { get; set; } }

    public class GenOrder
    {
        public int Id { get; set; }
        public GenCustomer? Customer { get; set; }
        public List<GenLine> Lines { get; set; } = new List<GenLine>();
        public GenStatus Status { get; set; }
        public int? Priority { get; set; }
        public DateTime Created { get; set; }
        public Dictionary<string, int> Tags { get; set; } = new Dictionary<string, int>();
        public HashSet<string> Labels { get; set; } = new HashSet<string>();
        public decimal GetTotal() => Lines?.Sum(l => l.Price * l.Quantity) ?? 0m;
    }

    public class GenLineDto { public string Sku { get; set; } = ""; public int Quantity { get; set; } public decimal Price { get; set; } }

    public class GenOrderDto
    {
        public int Id { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerAddressCity { get; set; }
        public GenLineDto[] Lines { get; set; } = Array.Empty<GenLineDto>();
        public GenStatusDto Status { get; set; }
        public int Priority { get; set; }
        public string Created { get; set; } = "";
        public Dictionary<string, long> Tags { get; set; } = new Dictionary<string, long>();
        public List<string> Labels { get; set; } = new List<string>();
        public decimal Total { get; set; }
        public string? Reference { get; set; }
    }

    public class GenPerson
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public int Age { get; set; }
        public string Secret { get; set; } = "";
    }

    public record GenPersonRecord(string FirstName, string LastName, int Age);

    public class GenNode
    {
        public string Name { get; set; } = "";
        public List<GenNode> Children { get; set; } = new List<GenNode>();
    }

    public class GenNodeDto
    {
        public string Name { get; set; } = "";
        public IReadOnlyList<GenNodeDto> Children { get; set; } = new List<GenNodeDto>();
    }

    [GeneratedMapper]
    public static partial class GenOrderMapper
    {
        [MapMember("Id", "Reference")]
        public static partial GenOrderDto ToDto(this GenOrder source);

        public static partial GenLineDto ToDto(GenLine line);

        public static partial List<GenLineDto> ToDtos(IEnumerable<GenLine> lines);

        public static partial GenStatusDto ToDto(GenStatus status);

        public static partial GenPersonRecord ToRecord(GenPerson person);

        public static partial void Update(GenPerson source, GenPerson target);

        public static partial GenNodeDto ToDto(GenNode node);
    }

    [GeneratedMapper]
    public partial class GenPersonCloner
    {
        [MapIgnore("Secret")]
        public partial GenPerson Clone(GenPerson source);
    }

    public class GenMoney { public decimal Amount { get; set; } public string Currency { get; set; } = "USD"; }
    public class GenProduct { public string Name { get; set; } = ""; public GenMoney Price { get; set; } = new GenMoney(); public decimal Weight { get; set; } }
    public class GenProductDto { public string Name { get; set; } = ""; public string Price { get; set; } = ""; public string Weight { get; set; } = ""; }

    [GeneratedMapper]
    public static partial class GenProductMapper
    {
        public static partial GenProductDto ToDto(GenProduct product);

        // Ordinary static methods are used for every member of these types.
        private static string Format(GenMoney money) => money.Amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + money.Currency;
        private static string Format(decimal value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public class GeneratedMapperTests
    {
        [Fact]
        public void User_conversion_methods_are_used_and_win_over_built_in_conversions()
        {
            var dto = GenProductMapper.ToDto(new GenProduct { Name = "Pen", Price = new GenMoney { Amount = 2.5m, Currency = "EUR" }, Weight = 5m });

            Assert.Equal("Pen", dto.Name);
            Assert.Equal("2.50 EUR", dto.Price);
            Assert.Equal("5.000", dto.Weight);
        }

        private static GenOrder SampleOrder() => new GenOrder
        {
            Id = 42,
            Customer = new GenCustomer { Name = "Ada", Address = new GenAddress { City = "London" } },
            Lines = new List<GenLine>
            {
                new GenLine { Sku = "A", Quantity = 2, Price = 1.5m },
                new GenLine { Sku = "B", Quantity = 1, Price = 10m }
            },
            Status = GenStatus.Shipped,
            Priority = 3,
            Created = new DateTime(2026, 10, 23, 12, 0, 0),
            Tags = new Dictionary<string, int> { ["vip"] = 1 },
            Labels = new HashSet<string> { "x" }
        };

        [Fact]
        public void Maps_members_flattening_collections_enums_and_get_methods()
        {
            var order = SampleOrder();

            var dto = order.ToDto();

            Assert.Equal(42, dto.Id);
            Assert.Equal("Ada", dto.CustomerName);
            Assert.Equal("London", dto.CustomerAddressCity);
            Assert.Equal(new[] { "A", "B" }, dto.Lines.Select(l => l.Sku));
            Assert.Equal(2, dto.Lines[0].Quantity);
            Assert.Equal(GenStatusDto.Shipped, dto.Status);
            Assert.Equal(3, dto.Priority);
            Assert.Equal(order.Created.ToString(), dto.Created);
            Assert.Equal(1L, dto.Tags["vip"]);
            Assert.Equal(new[] { "x" }, dto.Labels);
            Assert.Equal(13m, dto.Total);
            Assert.Equal("42", dto.Reference);
        }

        [Fact]
        public void Null_intermediate_objects_and_nullable_values_map_to_defaults()
        {
            var order = new GenOrder { Id = 1, Customer = null, Priority = null, Lines = null! };

            var dto = order.ToDto();

            Assert.Null(dto.CustomerName);
            Assert.Null(dto.CustomerAddressCity);
            Assert.Equal(0, dto.Priority);
            Assert.Empty(dto.Lines);
        }

        [Fact]
        public void Null_source_returns_null()
        {
            GenOrder? order = null;
            Assert.Null(GenOrderMapper.ToDto(order!));
        }

        [Fact]
        public void Maps_collections_at_the_root()
        {
            var dtos = GenOrderMapper.ToDtos(SampleOrder().Lines);
            Assert.Equal(2, dtos.Count);
            Assert.Equal("B", dtos[1].Sku);
        }

        [Fact]
        public void Maps_enums_by_name()
        {
            Assert.Equal(GenStatusDto.Cancelled, GenOrderMapper.ToDto(GenStatus.Cancelled));
            Assert.Throws<ArgumentOutOfRangeException>(() => GenOrderMapper.ToDto((GenStatus)99));
        }

        [Fact]
        public void Maps_into_record_constructors()
        {
            var record = GenOrderMapper.ToRecord(new GenPerson { FirstName = "Grace", LastName = "Hopper", Age = 85 });
            Assert.Equal(new GenPersonRecord("Grace", "Hopper", 85), record);
        }

        [Fact]
        public void Updates_an_existing_object()
        {
            var target = new GenPerson { FirstName = "old", Secret = "keep?" };
            GenOrderMapper.Update(new GenPerson { FirstName = "new", LastName = "L", Age = 5, Secret = "s" }, target);
            Assert.Equal("new", target.FirstName);
            Assert.Equal("L", target.LastName);
            Assert.Equal(5, target.Age);
            Assert.Equal("s", target.Secret);
        }

        [Fact]
        public void Instance_mappers_and_ignored_members()
        {
            var clone = new GenPersonCloner().Clone(new GenPerson { FirstName = "A", Secret = "hidden" });
            Assert.Equal("A", clone.FirstName);
            Assert.Equal("", clone.Secret);
        }

        [Fact]
        public void Maps_recursive_structures()
        {
            var tree = new GenNode { Name = "root", Children = { new GenNode { Name = "child", Children = { new GenNode { Name = "leaf" } } } } };
            var dto = GenOrderMapper.ToDto(tree);
            Assert.Equal("leaf", dto.Children[0].Children[0].Name);
        }
    }
}
