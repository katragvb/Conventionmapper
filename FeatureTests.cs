using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class TagEntity { public int Id { get; set; } public string Label { get; set; } = ""; }
    public class TagDto { public int Id { get; set; } public string Label { get; set; } = ""; }
    public class PostEntity { public List<TagEntity> Tags { get; set; } = new List<TagEntity>(); }
    public class PostDto { public List<TagDto> Tags { get; set; } = new List<TagDto>(); }

    public class UserEntity { public string Name { get; set; } = ""; public string Email { get; set; } = ""; public int Age { get; set; } }
    public class UserPatch { public string? Name { get; set; } public string? Email { get; set; } public int? Age { get; set; } }

    public enum Color { Red, Green, Blue, Purple }
    public enum ColorDto { Unknown, Red, Green, Blue, Violet }

    public class Inventory
    {
        public List<int> Ids { get; set; } = new List<int>();
        public Dictionary<string, int> Stock { get; set; } = new Dictionary<string, int>();
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class InventoryDto
    {
        public ImmutableArray<int> Ids { get; set; }
        public ImmutableDictionary<string, int> Stock { get; set; } = ImmutableDictionary<string, int>.Empty;
        public IImmutableList<string> Tags { get; set; } = ImmutableList<string>.Empty;
    }

    public class ItemSrc { public string Count { get; set; } = ""; }
    public class ItemDto { public int Count { get; set; } }
    public class ParentSrc { public List<ItemSrc> Items { get; set; } = new List<ItemSrc>(); }
    public class ParentDto { public List<ItemDto> Items { get; set; } = new List<ItemDto>(); }

    public class FeatureTests
    {
        [Fact]
        public void EqualityComparison_merges_existing_collection_items()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<PostDto, PostEntity>();
                cfg.CreateMap<TagDto, TagEntity>().EqualityComparison((dto, entity) => dto.Id == entity.Id);
            });
            var t1 = new TagEntity { Id = 1, Label = "a" };
            var t2 = new TagEntity { Id = 2, Label = "b" };
            var post = new PostEntity { Tags = { t1, t2 } };
            var originalList = post.Tags;

            config.CreateMapper().Map(new PostDto { Tags = { new TagDto { Id = 2, Label = "B" }, new TagDto { Id = 3, Label = "c" } } }, post);

            Assert.Same(originalList, post.Tags);
            Assert.Equal(2, post.Tags.Count);
            Assert.Same(t2, post.Tags[0]);          // updated in place
            Assert.Equal("B", t2.Label);
            Assert.Equal(3, post.Tags[1].Id);       // added
            Assert.DoesNotContain(t1, post.Tags);   // removed
        }

        [Fact]
        public void Patch_only_applies_non_null_values()
        {
            var mapper = new MapperConfiguration(cfg => cfg.CreateMap<UserPatch, UserEntity>()).CreateMapper();
            var user = new UserEntity { Name = "Ann", Email = "ann@old.com", Age = 30 };

            mapper.Patch(new UserPatch { Email = "ann@new.com" }, user);

            Assert.Equal("Ann", user.Name);
            Assert.Equal("ann@new.com", user.Email);
            Assert.Equal(30, user.Age);

            // A normal Map overwrites with nulls/defaults, for contrast
            var overwritten = mapper.Map(new UserPatch { Email = "x" }, new UserEntity { Name = "Ann", Age = 30 });
            Assert.Null(overwritten.Name);
            Assert.Equal(0, overwritten.Age);
        }

        [Fact]
        public void IgnoreNullSourceValues_on_the_map()
        {
            var mapper = new MapperConfiguration(cfg => cfg.CreateMap<UserPatch, UserEntity>().IgnoreNullSourceValues()).CreateMapper();
            var user = new UserEntity { Name = "Bo", Age = 5 };
            mapper.Map(new UserPatch { Age = 6 }, user);
            Assert.Equal("Bo", user.Name);
            Assert.Equal(6, user.Age);
        }

        [Fact]
        public void Strict_enum_mapping_reports_unmatched_values()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping());
            var ex = Assert.Throws<MapperConfigurationException>(() => config.AssertConfigurationIsValid());
            Assert.Contains("Purple", ex.Message);
        }

        [Fact]
        public void Strict_enum_mapping_with_overrides_maps_and_projects()
        {
            var config = new MapperConfiguration(cfg =>
                cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o.MapValue(Color.Purple, ColorDto.Violet)));
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();

            Assert.Equal(ColorDto.Red, mapper.Map<ColorDto>(Color.Red)); // by name, not by number
            Assert.Equal(ColorDto.Violet, mapper.Map<ColorDto>(Color.Purple));

            var projected = new[] { Color.Purple, Color.Blue }.AsQueryable().ProjectTo<ColorDto>(config).ToList();
            Assert.Equal(new[] { ColorDto.Violet, ColorDto.Blue }, projected);
        }

        [Fact]
        public void Strict_enum_mapping_with_fallback()
        {
            var config = new MapperConfiguration(cfg =>
                cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o.Fallback(ColorDto.Unknown)));
            config.AssertConfigurationIsValid();
            Assert.Equal(ColorDto.Unknown, config.CreateMapper().Map<ColorDto>(Color.Purple));
        }

        [Fact]
        public void Immutable_collections()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Inventory, InventoryDto>());
            config.AssertConfigurationIsValid();
            var dto = config.CreateMapper().Map<InventoryDto>(new Inventory
            {
                Ids = { 1, 2, 3 },
                Stock = { ["apples"] = 4 },
                Tags = { "x", "y" }
            });

            Assert.Equal(new[] { 1, 2, 3 }, dto.Ids.ToArray());
            Assert.Equal(4, dto.Stock["apples"]);
            Assert.IsType<ImmutableList<string>>(dto.Tags);
            Assert.Equal(2, dto.Tags.Count);
        }

        [Fact]
        public void Errors_include_the_full_member_path()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<ParentSrc, ParentDto>();
                cfg.CreateMap<ItemSrc, ItemDto>();
            });
            var source = new ParentSrc { Items = { new ItemSrc { Count = "1" }, new ItemSrc { Count = "not a number" } } };

            var ex = Assert.Throws<MappingException>(() => config.CreateMapper().Map<ParentDto>(source));

            Assert.Equal("ParentDto.Items[1].Count", ex.MemberPath);
            Assert.Contains("ParentDto.Items[1].Count", ex.Message);
        }

        [Fact]
        public void Explain_describes_each_member()
        {
            var config = new MapperConfiguration(cfg => cfg.AddProfile<OrderProfile>());
            var text = config.Explain<Order, OrderDto>();

            Assert.Contains("Order -> OrderDto", text);
            Assert.Contains("Customer.Name", text);
            Assert.Contains("GetTotal()", text);
            Assert.Contains("null substitute", text);
            Assert.Contains("items via map OrderLine -> OrderLineDto", text);
        }
    }
}
