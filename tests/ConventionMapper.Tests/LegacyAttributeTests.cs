using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class LegacySource { public string Name { get; set; } = ""; }

#pragma warning disable CS0618 // the obsolete 1.x attribute must keep working
    [AutoMap(typeof(LegacySource))]
#pragma warning restore CS0618
    public class LegacyDest { public string Name { get; set; } = ""; }

    public class LegacyAttributeTests
    {
        [Fact]
        public void The_1x_attribute_is_still_honoured()
        {
            var mapper = new MapperConfiguration(cfg => cfg.AddMaps(typeof(LegacyDest).Assembly)).CreateMapper();
            Assert.Equal("a", mapper.Map<LegacySource, LegacyDest>(new LegacySource { Name = "a" }).Name);
        }
    }
}
