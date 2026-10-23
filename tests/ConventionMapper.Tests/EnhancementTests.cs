using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class ShopCustomer { public string Name { get; set; } = ""; }
    public class ShopLine { public string Sku { get; set; } = ""; public int Qty { get; set; } }
    public class ShopOrder
    {
        public int Id { get; set; }
        public ShopCustomer? Customer { get; set; } = new ShopCustomer();
        public List<ShopLine>? Lines { get; set; } = new List<ShopLine>();
        public int[] Codes { get; set; } = Array.Empty<int>();
    }

    public class ShopCustomerDto { public string Name { get; set; } = ""; }
    public class ShopLineDto { public string Sku { get; set; } = ""; public int Qty { get; set; } }
    public class ShopOrderDto
    {
        public int Id { get; set; }
        public string? CustomerName { get; set; }
        public ShopCustomerDto? Customer { get; set; }
        public List<ShopLineDto> Lines { get; set; } = new List<ShopLineDto>();
        public List<int> Codes { get; set; } = new List<int>();
    }

    public class AltLine { public string Code { get; set; } = ""; public int Count { get; set; } }

    public class ThrowingSource { public int Value => throw new InvalidOperationException("boom"); }
    public class ValueDto { public int Value { get; set; } }

    public class AuthorEntity { public string Name { get; set; } = ""; public List<BookEntity> Books { get; set; } = new List<BookEntity>(); }
    public class BookEntity { public string Title { get; set; } = ""; }
    public class AuthorDto { public string Name { get; set; } = ""; public List<BookDto>? Books { get; set; } }
    public class BookDto { public string Title { get; set; } = ""; }

#nullable enable
    public class LooseSource { public string? Nickname { get; set; } public int? Score { get; set; } }
    public class StrictDest { public string Nickname { get; set; } = ""; public int Score { get; set; } }
#nullable restore

    public class EnhancementTests
    {
        private static ShopOrder NewOrder() => new ShopOrder
        {
            Id = 5,
            Customer = new ShopCustomer { Name = "Grace" },
            Lines = new List<ShopLine> { new ShopLine { Sku = "A1", Qty = 2 }, new ShopLine { Sku = "B2", Qty = 1 } },
            Codes = new[] { 7, 8, 9 }
        };

        private static MapperConfiguration ShopConfig(bool compiled) => new MapperConfiguration(cfg =>
        {
            cfg.UseCompiledMappings = compiled;
            cfg.CreateMap<ShopOrder, ShopOrderDto>();
            cfg.CreateMap<ShopLine, ShopLineDto>();
            cfg.CreateMap<ShopCustomer, ShopCustomerDto>();
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Nested_objects_and_collections_map_the_same_compiled_or_not(bool compiled)
        {
            var config = ShopConfig(compiled);
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();

            for (var i = 0; i < 2; i++) // second call uses the static fast path when compiled
            {
                var dto = mapper.Map<ShopOrder, ShopOrderDto>(NewOrder());
                Assert.Equal(5, dto.Id);
                Assert.Equal("Grace", dto.CustomerName);
                Assert.Equal("Grace", dto.Customer!.Name);
                Assert.Equal(new[] { "A1", "B2" }, dto.Lines.Select(l => l.Sku));
                Assert.Equal(new[] { 7, 8, 9 }, dto.Codes);
            }
        }

        [Fact]
        public void Nested_objects_and_collections_are_inlined_into_one_context_free_delegate()
        {
            var config = ShopConfig(true);
            config.CreateMapper().Map<ShopOrder, ShopOrderDto>(NewOrder());
            Assert.True(config.FindTypeMapFor<ShopOrder, ShopOrderDto>()!.IsContextFree);
        }

        [Fact]
        public void Inlined_collections_refill_existing_instances_and_handle_nulls()
        {
            var mapper = ShopConfig(true).CreateMapper();

            var target = new ShopOrderDto();
            var originalLines = target.Lines;
            mapper.Map(NewOrder(), target);
            Assert.Same(originalLines, target.Lines);
            Assert.Equal(2, target.Lines.Count);

            var empty = mapper.Map<ShopOrder, ShopOrderDto>(new ShopOrder { Customer = null, Lines = null });
            Assert.Null(empty.Customer);
            Assert.Null(empty.CustomerName);
            Assert.NotNull(empty.Lines);
            Assert.Empty(empty.Lines);
        }

        [Fact]
        public void Object_overload_fast_path_supports_several_source_types()
        {
            var mapper = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<ShopLine, ShopLineDto>();
                cfg.CreateMap<AltLine, ShopLineDto>()
                    .ForMember(d => d.Sku, o => o.MapFrom(s => s.Code))
                    .ForMember(d => d.Qty, o => o.MapFrom(s => s.Count));
            }).CreateMapper();

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("A", mapper.Map<ShopLineDto>(new ShopLine { Sku = "A", Qty = 1 }).Sku);
                Assert.Equal(9, mapper.Map<ShopLineDto>(new AltLine { Code = "Z", Count = 9 }).Qty);
            }
        }

        [Fact]
        public void Fast_path_failures_still_report_the_exact_member()
        {
            var mapper = new MapperConfiguration(cfg => cfg.CreateMap<ThrowingSource, ValueDto>()).CreateMapper();
            for (var i = 0; i < 2; i++) // first call fills the cache, second goes through the fast path
            {
                var ex = Assert.Throws<MappingException>(() => mapper.Map<ThrowingSource, ValueDto>(new ThrowingSource()));
                Assert.Equal("ValueDto.Value", ex.MemberPath);
                Assert.IsType<InvalidOperationException>(ex.InnerException);
            }
        }

        [Fact]
        public void ProjectTo_parameters_replace_captured_variables()
        {
            var minAge = 0;
            var config = new MapperConfiguration(cfg => cfg.CreateMap<PersonEntity, PersonSummary>()
                .ForMember(d => d.FullName, o => o.MapFrom(s => s.Age >= minAge ? "adult" : "minor")));
            var people = new[] { new PersonEntity { Age = 10 }, new PersonEntity { Age = 30 } }.AsQueryable();

            Assert.Equal(new[] { "adult", "adult" }, people.ProjectTo<PersonSummary>(config).Select(p => p.FullName).ToArray());
            Assert.Equal(new[] { "minor", "adult" }, people.ProjectTo<PersonSummary>(config, new { minAge = 18 }).Select(p => p.FullName).ToArray());
            Assert.Equal(new[] { "minor", "minor" },
                people.ProjectTo<PersonSummary>(config, new Dictionary<string, object> { ["minAge"] = 50 }).Select(p => p.FullName).ToArray());
        }

        [Fact]
        public void ExplicitExpansion_members_are_only_projected_when_requested()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<AuthorEntity, AuthorDto>().ForMember(d => d.Books, o => o.ExplicitExpansion());
                cfg.CreateMap<BookEntity, BookDto>();
            });
            config.AssertConfigurationIsValid();
            var authors = new[] { new AuthorEntity { Name = "Le Guin", Books = { new BookEntity { Title = "Earthsea" } } } }.AsQueryable();

            Assert.Null(authors.ProjectTo<AuthorDto>(config).Single().Books);
            Assert.Equal("Earthsea", authors.ProjectTo<AuthorDto>(config, null, d => d.Books!).Single().Books!.Single().Title);
            Assert.Single(authors.ProjectTo<AuthorDto>(config, null, "Books").Single().Books!);
            Assert.Single(config.CreateMapper().Map<AuthorDto>(authors.First()).Books!); // Map() always maps it
            Assert.Contains("only when expanded", config.Explain<AuthorEntity, AuthorDto>());
        }

        [Fact]
        public void ValidateNullability_reports_nullable_sources_for_non_nullable_destinations()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.ValidateNullability = true;
                cfg.CreateMap<LooseSource, StrictDest>();
            });
            var ex = Assert.Throws<MapperConfigurationException>(() => config.AssertConfigurationIsValid());
            Assert.Contains("'Score'", ex.Message);
#if NET6_0_OR_GREATER
            Assert.Contains("'Nickname'", ex.Message);
#endif

            new MapperConfiguration(cfg =>
            {
                cfg.ValidateNullability = true;
                cfg.CreateMap<LooseSource, StrictDest>()
                    .ForMember(d => d.Nickname, o => o.NullSubstitute(""))
                    .ForMember(d => d.Score, o => o.NullSubstitute(0));
            }).AssertConfigurationIsValid();

            new MapperConfiguration(cfg => cfg.CreateMap<LooseSource, StrictDest>()).AssertConfigurationIsValid(); // off by default
        }
    }
}
