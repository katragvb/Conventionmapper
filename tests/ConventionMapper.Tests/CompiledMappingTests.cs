using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class CompiledMappingTests
    {
        private static Order NewOrder() => new Order
        {
            Id = 11,
            Customer = new Customer { Name = "Ada", Address = new Address { City = "London" } },
            Lines = { new OrderLine { Product = "Book", Quantity = 2, Price = 10m }, new OrderLine { Product = "Pen", Quantity = 1, Price = 2.5m } },
            Status = OrderStatus.Delivered
        };

        private static MapperConfiguration Config(bool compiled) => new MapperConfiguration(cfg =>
        {
            cfg.UseCompiledMappings = compiled;
            cfg.AddProfile<OrderProfile>();
        });

        [Fact]
        public void Compiled_and_interpreted_results_are_identical()
        {
            var fast = Config(true).CreateMapper().Map<OrderDto>(NewOrder());
            var slow = Config(false).CreateMapper().Map<OrderDto>(NewOrder());

            Assert.Equal(slow.Id, fast.Id);
            Assert.Equal(slow.CustomerName, fast.CustomerName);
            Assert.Equal(slow.CustomerAddressCity, fast.CustomerAddressCity);
            Assert.Equal(slow.Total, fast.Total);
            Assert.Equal(slow.Status, fast.Status);
            Assert.Equal(slow.Notes, fast.Notes);
            Assert.Equal(slow.Lines.Select(l => l.Product + l.Quantity), fast.Lines.Select(l => l.Product + l.Quantity));
        }

        [Fact]
        public void Maps_are_compiled_on_first_use_and_simple_maps_are_context_free()
        {
            var config = Config(true);
            var mapper = config.CreateMapper();
            var lineMap = config.FindTypeMapFor<OrderLine, OrderLineDto>()!;
            Assert.False(lineMap.IsCompiled);

            mapper.Map<OrderDto>(NewOrder());

            Assert.True(lineMap.IsCompiled);
            Assert.True(lineMap.IsContextFree);                                   // Product, Quantity: direct copies
            Assert.True(config.FindTypeMapFor<Order, OrderDto>()!.IsCompiled);    // mixed: direct + engine calls
        }

        [Fact]
        public void Fast_path_returns_new_instances_each_call()
        {
            var mapper = Config(true).CreateMapper();
            var line = new OrderLine { Product = "Cup", Quantity = 3 };
            var a = mapper.Map<OrderLine, OrderLineDto>(line);
            var b = mapper.Map<OrderLine, OrderLineDto>(line);   // served from the static cache
            var c = mapper.Map<OrderLineDto>(line);
            var d = mapper.Map<OrderLineDto>(line);              // served from the static cache
            Assert.NotSame(a, b);
            Assert.NotSame(c, d);
            Assert.All(new[] { a, b, c, d }, x => { Assert.Equal("Cup", x.Product); Assert.Equal(3, x.Quantity); });
        }

        [Fact]
        public void Fast_path_cache_never_leaks_between_configurations()
        {
            var copyAll = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonEntity>()).CreateMapper();
            var skipName = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonEntity>().ForMember(d => d.FirstName, o => o.Ignore())).CreateMapper();
            var person = new PersonEntity { FirstName = "Joan", LastName = "Clarke", Age = 37 };

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("Joan", copyAll.Map<PersonEntity, PersonEntity>(person).FirstName);
                Assert.Equal("", skipName.Map<PersonEntity, PersonEntity>(person).FirstName);
            }
        }

        [Fact]
        public void Records_are_built_with_a_compiled_constructor_call()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonRecord>());
            var mapper = config.CreateMapper();
            for (var i = 0; i < 2; i++)
                Assert.Equal(new PersonRecord("Alan", "Turing", 41), mapper.Map<PersonEntity, PersonRecord>(new PersonEntity { FirstName = "Alan", LastName = "Turing", Age = 41 }));
            Assert.True(config.FindTypeMapFor<PersonEntity, PersonRecord>()!.IsContextFree);
        }

        [Fact]
        public void CompileMappings_compiles_eagerly_and_can_be_disabled()
        {
            var eager = Config(true);
            eager.CompileMappings();
            Assert.True(eager.FindTypeMapFor<Order, OrderDto>()!.IsCompiled);

            var off = Config(false);
            off.CompileMappings();
            off.CreateMapper().Map<OrderDto>(NewOrder());
            Assert.False(off.FindTypeMapFor<Order, OrderDto>()!.IsCompiled);
        }
    }
}
