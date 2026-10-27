using System;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public enum Shade { Red, Green, Blue, Purple, Magenta }
    public enum ShadeDto { Unknown, Red, Green, Blue, Violet }
    public enum Level { Low = 1, Medium = 2, High = 3 }
    public enum LevelCode { L1 = 1, L2 = 2, L3 = 3 }
    public enum Casing { red, green }
    public enum CasingDto { Red, Green }

    /// <summary>ReverseMap() for ConvertUsingEnumMapping: the reverse map uses the same matching with the overrides inverted.</summary>
    public class EnumReverseMapTests
    {
        [Fact]
        public void Overrides_are_inverted_and_names_still_match()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o
                .MapValue(Color.Purple, ColorDto.Violet)
                .ReverseMap(fallback: Color.Red)));
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();

            Assert.Equal(ColorDto.Violet, mapper.Map<ColorDto>(Color.Purple));
            Assert.Equal(Color.Purple, mapper.Map<Color>(ColorDto.Violet));   // inverted override
            Assert.Equal(Color.Blue, mapper.Map<Color>(ColorDto.Blue));       // by name
            Assert.Equal(Color.Red, mapper.Map<Color>(ColorDto.Unknown));     // reverse fallback
        }

        [Fact]
        public void Reverse_without_a_fallback_reports_unmatched_destination_values()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o
                .MapValue(Color.Purple, ColorDto.Violet)
                .ReverseMap()));
            var error = Assert.Throws<MapperConfigurationException>(() => config.AssertConfigurationIsValid());
            Assert.Contains("ColorDto.Unknown has no matching Color value", error.Message);
            Assert.DoesNotContain("Violet has no matching", error.Message);

            var thrown = Assert.Throws<MappingException>(() => config.CreateMapper().Map<Color>(ColorDto.Unknown));
            Assert.Contains("ColorDto", thrown.Message + thrown.InnerException?.Message);
        }

        [Fact]
        public void Forward_fallback_is_not_inverted()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o
                .Fallback(ColorDto.Unknown)
                .ReverseMap(fallback: Color.Green)));
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();
            Assert.Equal(ColorDto.Unknown, mapper.Map<ColorDto>(Color.Purple));
            Assert.Equal(Color.Green, mapper.Map<Color>(ColorDto.Unknown));   // the reverse fallback, not Purple
            Assert.Equal(Color.Green, mapper.Map<Color>(ColorDto.Violet));
        }

        [Fact]
        public void Two_overrides_onto_one_value_cannot_be_inverted()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Shade, ShadeDto>().ConvertUsingEnumMapping(o => o
                .MapValue(Shade.Purple, ShadeDto.Violet)
                .MapValue(Shade.Magenta, ShadeDto.Violet)
                .ReverseMap(fallback: Shade.Red)));
            var error = Assert.Throws<MapperConfigurationException>(() => config.AssertConfigurationIsValid());
            Assert.Contains("ShadeDto.Violet is mapped from Shade.Purple and Shade.Magenta by MapValue, so the reverse map can't choose one", error.Message);

            // The forward map is fine; the ambiguous value is never guessed, not even with a fallback.
            var mapper = config.CreateMapper();
            Assert.Equal(ShadeDto.Violet, mapper.Map<ShadeDto>(Shade.Magenta));
            Assert.Equal(Shade.Red, mapper.Map<Shade>(ShadeDto.Unknown));
            Assert.Throws<MappingException>(() => mapper.Map<Shade>(ShadeDto.Violet));
        }

        [Fact]
        public void By_value_and_case_insensitive_matching_carry_over()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Level, LevelCode>().ConvertUsingEnumMapping(o => o.MapByValue().ReverseMap());
                cfg.CreateMap<Casing, CasingDto>().ConvertUsingEnumMapping(o => o.MapByName(ignoreCase: true).ReverseMap());
            });
            config.AssertConfigurationIsValid();
            var mapper = config.CreateMapper();
            Assert.Equal(Level.High, mapper.Map<Level>(LevelCode.L3));
            Assert.Equal(Casing.green, mapper.Map<Casing>(CasingDto.Green));
        }

        [Fact]
        public void ReverseMap_on_the_map_itself_inverts_too_in_either_order()
        {
            var after = new MapperConfiguration(cfg =>
            {
                var map = cfg.CreateMap<Color, ColorDto>();
                map.ConvertUsingEnumMapping(o => o.MapValue(Color.Purple, ColorDto.Violet).Fallback(ColorDto.Unknown));
                map.ReverseMap().ConvertUsingEnumMapping(o => o.Fallback(Color.Red)); // the reverse map's own options win
            });
            var before = new MapperConfiguration(cfg =>
            {
                var map = cfg.CreateMap<Color, ColorDto>();
                map.ReverseMap();
                map.ConvertUsingEnumMapping(o => o.MapValue(Color.Purple, ColorDto.Violet));
            });

            Assert.Equal(Color.Red, after.CreateMapper().Map<Color>(ColorDto.Violet)); // explicit reverse config replaced the inversion
            Assert.Equal(Color.Purple, before.CreateMapper().Map<Color>(ColorDto.Violet));
            Assert.Equal(Color.Green, before.CreateMapper().Map<Color>(ColorDto.Green));
        }

        [Fact]
        public void Options_ReverseMap_after_map_ReverseMap_does_not_duplicate_the_map()
        {
            var config = new MapperConfiguration(cfg =>
            {
                var map = cfg.CreateMap<Color, ColorDto>();
                map.ReverseMap();
                map.ConvertUsingEnumMapping(o => o.MapValue(Color.Purple, ColorDto.Violet).ReverseMap(Color.Red));
            });
            config.AssertConfigurationIsValid();
            Assert.Equal(Color.Purple, config.CreateMapper().Map<Color>(ColorDto.Violet));
            Assert.Equal(Color.Red, config.CreateMapper().Map<Color>(ColorDto.Unknown));
        }

        [Fact]
        public void Reverse_enum_map_projects()
        {
            var config = new MapperConfiguration(cfg => cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o
                .MapValue(Color.Purple, ColorDto.Violet)
                .ReverseMap(Color.Red)));
            var projected = new[] { ColorDto.Violet, ColorDto.Blue, ColorDto.Unknown }.AsQueryable().ProjectTo<Color>(config).ToList();
            Assert.Equal(new[] { Color.Purple, Color.Blue, Color.Red }, projected);
        }

        [Fact]
        public void Enum_members_of_reversed_object_maps_use_the_inverted_mapping()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Color, ColorDto>().ConvertUsingEnumMapping(o => o.MapValue(Color.Purple, ColorDto.Violet).ReverseMap(Color.Red));
                cfg.CreateMap<Paint, PaintDto>().ReverseMap();
            });
            config.AssertConfigurationIsValid();
            var paint = config.CreateMapper().Map<Paint>(new PaintDto { Name = "x", Color = ColorDto.Violet });
            Assert.Equal(Color.Purple, paint.Color);
        }

        public class Paint { public string Name { get; set; } = ""; public Color Color { get; set; } }
        public class PaintDto { public string Name { get; set; } = ""; public ColorDto Color { get; set; } }
    }
}
