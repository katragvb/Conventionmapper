using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;
using Microsoft.Extensions.DependencyInjection;

namespace ConventionMapper.Tests
{
    // ----------------------------------------------------------------- models

    public class Customer { public string Name { get; set; } = ""; public Address Address { get; set; } = new Address(); }
    public class Address { public string City { get; set; } = ""; public string Street { get; set; } = ""; }
    public class OrderLine { public string Product { get; set; } = ""; public int Quantity { get; set; } public decimal Price { get; set; } }
    public enum OrderStatus { New, Shipped, Delivered }
    public enum OrderStatusDto { New, Shipped, Delivered }

    public class Order
    {
        public int Id { get; set; }
        public Customer Customer { get; set; } = new Customer();
        public List<OrderLine> Lines { get; set; } = new List<OrderLine>();
        public OrderStatus Status { get; set; }
        public decimal GetTotal() => Lines.Sum(l => l.Price * l.Quantity);
        public string? Notes { get; set; }
    }

    public class OrderLineDto { public string Product { get; set; } = ""; public int Quantity { get; set; } }

    public class OrderDto
    {
        public int Id { get; set; }
        public string CustomerName { get; set; } = "";
        public string CustomerAddressCity { get; set; } = "";
        public OrderLineDto[] Lines { get; set; } = Array.Empty<OrderLineDto>();
        public OrderStatusDto Status { get; set; }
        public decimal Total { get; set; }
        public string Notes { get; set; } = "";
    }

    public record PersonRecord(string FirstName, string LastName, int Age);
    public class PersonEntity { public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; public int Age { get; set; } }

    public abstract class Shape { public string Name { get; set; } = ""; }
    public class Circle : Shape { public double Radius { get; set; } }
    public class Square : Shape { public double Side { get; set; } }
    public abstract class ShapeDto { public string Name { get; set; } = ""; }
    public class CircleDto : ShapeDto { public double Radius { get; set; } }
    public class SquareDto : ShapeDto { public double Side { get; set; } }

    public class TreeNode { public string Name { get; set; } = ""; public TreeNode? Parent { get; set; } public List<TreeNode> Children { get; set; } = new List<TreeNode>(); }
    public class TreeNodeDto { public string Name { get; set; } = ""; public TreeNodeDto? Parent { get; set; } public List<TreeNodeDto> Children { get; set; } = new List<TreeNodeDto>(); }

    public class Wrapper<T> { public T Value { get; set; } = default!; }
    public class WrapperDto<T> { public T Value { get; set; } = default!; }

    public class FullNameResolver : IValueResolver<PersonEntity, PersonSummary, string>
    {
        public string Resolve(PersonEntity source, PersonSummary destination, string destMember, ResolutionContext context) => source.FirstName + " " + source.LastName;
    }

    public class PersonSummary { public string FullName { get; set; } = ""; public int Age { get; set; } }

    public class UpperConverter : IValueConverter<string, string>
    {
        public string Convert(string sourceMember, ResolutionContext context) => sourceMember?.ToUpperInvariant() ?? "";
    }

    public class DateTimeToStringConverter : ITypeConverter<DateTime, string>
    {
        public string Convert(DateTime source, string destination, ResolutionContext context) => source.ToString("yyyy-MM-dd");
    }

    public class OrderProfile : Profile
    {
        public OrderProfile()
        {
            CreateMap<Order, OrderDto>()
                .ForMember(d => d.Notes, o => o.NullSubstitute("(none)"));
            CreateMap<OrderLine, OrderLineDto>();
        }
    }

    [MappedFrom(typeof(PersonEntity), ReverseMap = true)]
    public class PersonAttributeDto { public string FirstName { get; set; } = ""; [IgnoreMap] public string Secret { get; set; } = ""; [SourceMember("LastName")] public string Surname { get; set; } = ""; }

    // ----------------------------------------------------------------- tests

    public class MapperTests
    {
        private static Order SampleOrder() => new Order
        {
            Id = 7,
            Customer = new Customer { Name = "Ada", Address = new Address { City = "London", Street = "Main" } },
            Lines = { new OrderLine { Product = "Book", Quantity = 2, Price = 10m }, new OrderLine { Product = "Pen", Quantity = 1, Price = 2.5m } },
            Status = OrderStatus.Shipped
        };

        [Fact]
        public void Flattening_methods_collections_enums_and_null_substitute()
        {
            var config = new MapperConfiguration(cfg => cfg.AddProfile<OrderProfile>());
            config.AssertConfigurationIsValid();
            var dto = config.CreateMapper().Map<OrderDto>(SampleOrder());

            Assert.Equal(7, dto.Id);
            Assert.Equal("Ada", dto.CustomerName);
            Assert.Equal("London", dto.CustomerAddressCity);
            Assert.Equal(22.5m, dto.Total);
            Assert.Equal(OrderStatusDto.Shipped, dto.Status);
            Assert.Equal(2, dto.Lines.Length);
            Assert.Equal("Pen", dto.Lines[1].Product);
            Assert.Equal("(none)", dto.Notes);
        }

        [Fact]
        public void Validation_reports_unmapped_members()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonSummary>());
            var ex = Assert.Throws<MapperConfigurationException>(() => config.AssertConfigurationIsValid());
            Assert.Contains("FullName", ex.Message);
        }

        [Fact]
        public void Value_resolver_and_ignore()
        {
            var config = new MapperConfiguration(cfg =>
                cfg.CreateMap<PersonEntity, PersonSummary>().ForMember(d => d.FullName, o => o.MapFrom<FullNameResolver>()));
            config.AssertConfigurationIsValid();
            var summary = config.CreateMapper().Map<PersonSummary>(new PersonEntity { FirstName = "Grace", LastName = "Hopper", Age = 85 });
            Assert.Equal("Grace Hopper", summary.FullName);
            Assert.Equal(85, summary.Age);
        }

        [Fact]
        public void ReverseMap_unflattens()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Order, OrderDto>().ReverseMap());
            var mapper = config.CreateMapper();
            var order = mapper.Map<Order>(new OrderDto { Id = 3, CustomerName = "Linus", CustomerAddressCity = "Helsinki" });
            Assert.Equal(3, order.Id);
            Assert.Equal("Linus", order.Customer.Name);
            Assert.Equal("Helsinki", order.Customer.Address.City);
        }

        [Fact]
        public void Records_use_constructor_mapping()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<PersonEntity, PersonRecord>();
                cfg.CreateMap<PersonRecord, PersonEntity>();
            });
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();
            var record = mapper.Map<PersonRecord>(new PersonEntity { FirstName = "Alan", LastName = "Turing", Age = 41 });
            Assert.Equal(new PersonRecord("Alan", "Turing", 41), record);
            Assert.Equal("Turing", mapper.Map<PersonEntity>(record).LastName);
        }

        [Fact]
        public void Inheritance_with_Include_is_polymorphic()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Shape, ShapeDto>().Include<Circle, CircleDto>().Include<Square, SquareDto>();
                cfg.CreateMap<Circle, CircleDto>();
                cfg.CreateMap<Square, SquareDto>();
            });
            config.AssertConfigurationIsValid();
            var shapes = new List<Shape> { new Circle { Name = "c", Radius = 2 }, new Square { Name = "s", Side = 3 } };
            var dtos = config.CreateMapper().Map<List<ShapeDto>>(shapes);
            Assert.IsType<CircleDto>(dtos[0]);
            Assert.Equal(3, ((SquareDto)dtos[1]).Side);
        }

        [Fact]
        public void Circular_references_are_preserved()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<TreeNode, TreeNodeDto>());
            var root = new TreeNode { Name = "root" };
            root.Children.Add(new TreeNode { Name = "child", Parent = root });
            var dto = config.CreateMapper().Map<TreeNodeDto>(root);
            Assert.Same(dto, dto.Children[0].Parent);
        }

        [Fact]
        public void Before_after_map_conditions_and_items()
        {
            var config = new MapperConfiguration(cfg =>
                cfg.CreateMap<PersonEntity, PersonSummary>()
                    .ForMember(d => d.FullName, o => o.MapFrom((s, d, m, ctx) => s.FirstName + " #" + ctx.Items["Tag"]))
                    .ForMember(d => d.Age, o => o.Condition(s => s.Age >= 0))
                    .AfterMap((s, d) => d.FullName = d.FullName.Trim()));
            var mapper = config.CreateMapper();
            var result = mapper.Map<PersonSummary>(new PersonEntity { FirstName = "Bob", Age = -1 }, opt => opt.Items["Tag"] = 5);
            Assert.Equal("Bob #5", result.FullName);
            Assert.Equal(0, result.Age);
        }

        [Fact]
        public void Type_converters_and_value_converters()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<DateTime, string>().ConvertUsing<DateTimeToStringConverter>();
                cfg.CreateMap<PersonEntity, PersonSummary>()
                    .ForMember(d => d.FullName, o => o.ConvertUsing(new UpperConverter(), s => s.LastName));
            });
            var mapper = config.CreateMapper();
            Assert.Equal("2024-01-02", mapper.Map<string>(new DateTime(2024, 1, 2)));
            Assert.Equal("LOVELACE", mapper.Map<PersonSummary>(new PersonEntity { LastName = "Lovelace" }).FullName);
        }

        [Fact]
        public void Map_into_existing_object_updates_in_place()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonEntity>());
            var target = new PersonEntity { FirstName = "Old" };
            var result = config.CreateMapper().Map(new PersonEntity { FirstName = "New", Age = 3 }, target);
            Assert.Same(target, result);
            Assert.Equal("New", target.FirstName);
        }

        [Fact]
        public void Open_generics()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap(typeof(Wrapper<>), typeof(WrapperDto<>)));
            var dto = config.CreateMapper().Map<WrapperDto<int>>(new Wrapper<int> { Value = 42 });
            Assert.Equal(42, dto.Value);
        }

        [Fact]
        public void Dictionaries_and_primitive_conversions()
        {
            var mapper = new MapperConfiguration(cfg => { }).CreateMapper();
            var dict = mapper.Map<Dictionary<string, int>>(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
            Assert.Equal(2, dict["b"]);
            Assert.Equal(OrderStatus.Delivered, mapper.Map<OrderStatus>("delivered"));
            Assert.Equal(Guid.Empty, mapper.Map<Guid>(Guid.Empty.ToString()));
            Assert.Empty(mapper.Map<List<int>>(null));
        }

        [Fact]
        public void Value_transformers_and_naming_conventions()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.ValueTransformers.Add<string>(s => s.Trim());
                cfg.SourceMemberNamingConvention = LowerUnderscoreNamingConvention.Instance;
                cfg.CreateMap<SnakeSource, PersonEntity>().ForMember(d => d.Age, o => o.Ignore());
            });
            var person = config.CreateMapper().Map<PersonEntity>(new SnakeSource { first_name = "  Ken  ", last_name = "Thompson" });
            Assert.Equal("Ken", person.FirstName);
            Assert.Equal("Thompson", person.LastName);
        }

        public class SnakeSource { public string first_name { get; set; } = ""; public string last_name { get; set; } = ""; }

        [Fact]
        public void ProjectTo_builds_a_translatable_select()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Order, OrderDto>().ForMember(d => d.Notes, o => o.MapFrom(s => s.Notes ?? "(none)"));
                cfg.CreateMap<OrderLine, OrderLineDto>();
            });
            var query = new[] { SampleOrder() }.AsQueryable().ProjectTo<OrderDto>(config);
            var dto = query.Single();
            Assert.Equal("Ada", dto.CustomerName);
            Assert.Equal(22.5m, dto.Total);
            Assert.Equal(2, dto.Lines.Length);
            Assert.Equal("(none)", dto.Notes);
            Assert.Contains("Select", query.Expression.ToString());
        }

        [Fact]
        public void Attribute_maps_are_discovered()
        {
            var config = new MapperConfiguration(cfg => cfg.AddMaps(typeof(MapperTests).Assembly));
            var dto = config.CreateMapper().Map<PersonAttributeDto>(new PersonEntity { FirstName = "Edsger", LastName = "Dijkstra" });
            Assert.Equal("Dijkstra", dto.Surname);
            Assert.Equal("", dto.Secret);
        }

        [Fact]
        public void Dependency_injection_registration()
        {
            var services = new ServiceCollection();
            services.AddConventionMapper(cfg => cfg.CreateMap<PersonEntity, PersonSummary>()
                .ForMember(d => d.FullName, o => o.MapFrom<FullNameResolver>()), typeof(MapperTests));
            using var provider = services.BuildServiceProvider();
            var mapper = provider.GetRequiredService<IMapper>();
            Assert.Equal("A B", mapper.Map<PersonSummary>(new PersonEntity { FirstName = "A", LastName = "B" }).FullName);
            Assert.NotNull(provider.GetService<FullNameResolver>());
        }

        [Fact]
        public void Missing_map_throws_mapping_exception()
        {
            var mapper = new MapperConfiguration(cfg => { }).CreateMapper();
            Assert.Throws<MappingException>(() => mapper.Map<PersonSummary>(new PersonEntity()));
        }
    }
}
