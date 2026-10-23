using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class PoolSource { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class PoolDest { public int Id { get; set; } public string Name { get; set; } = ""; public string? Tag { get; set; } }

    public class PoolNode { public string Name { get; set; } = ""; public PoolNode? Next { get; set; } }
    public class PoolNodeDto { public string Name { get; set; } = ""; public PoolNodeDto? Next { get; set; } }

    public class ContextPoolingTests
    {
        private static IMapper CreateMapper(bool pooling) =>
            new MapperConfiguration(cfg =>
            {
                if (!pooling) cfg.DisableContextPooling();
                cfg.CreateMap<PoolSource, PoolDest>()
                   .ForMember(d => d.Tag, o => o.MapFrom((s, d, m, ctx) => ctx.Items.TryGetValue("Tag", out var tag) ? (string)tag : null));
            }).CreateMapper();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Items_do_not_leak_into_the_next_call(bool pooling)
        {
            var mapper = CreateMapper(pooling);
            var source = new PoolSource { Id = 1, Name = "a" };

            var first = mapper.Map<PoolDest>(source, opt => opt.Items["Tag"] = "first");
            var second = mapper.Map<PoolSource, PoolDest>(source);

            Assert.Equal("first", first.Tag);
            Assert.Null(second.Tag);
        }

        [Fact]
        public void Items_added_by_a_resolver_are_cleared_between_calls()
        {
            var mapper = new MapperConfiguration(cfg =>
                cfg.CreateMap<PoolSource, PoolDest>()
                   .ForMember(d => d.Tag, o => o.MapFrom((s, d, m, ctx) =>
                   {
                       var seen = ctx.Items.ContainsKey("seen") ? "dirty" : "clean";
                       ctx.Items["seen"] = true;
                       return seen;
                   }))).CreateMapper();

            Assert.Equal("clean", mapper.Map<PoolSource, PoolDest>(new PoolSource()).Tag);
            Assert.Equal("clean", mapper.Map<PoolSource, PoolDest>(new PoolSource()).Tag);
        }

        [Fact]
        public void Reentrant_map_calls_on_the_same_thread_get_their_own_context()
        {
            IMapper? outer = null;
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<PoolSource, PoolDest>()
                   .ForMember(d => d.Tag, o => o.MapFrom((s, d, m, ctx) =>
                   {
                       if (s.Id == 0) return "inner:" + (ctx.Items.TryGetValue("Tag", out var t) ? t : "none");
                       var inner = outer!.Map<PoolDest>(new PoolSource { Id = 0 }, opt => opt.Items["Tag"] = "x");
                       return "outer:" + ctx.Items["Tag"] + "/" + inner.Tag;
                   }));
            });
            outer = config.CreateMapper();

            var result = outer.Map<PoolDest>(new PoolSource { Id = 1 }, opt => opt.Items["Tag"] = "y");

            Assert.Equal("outer:y/inner:x", result.Tag);
        }

        [Fact]
        public void Reference_cache_is_not_shared_between_calls()
        {
            var mapper = new MapperConfiguration(cfg => cfg.CreateMap<PoolNode, PoolNodeDto>()).CreateMapper();
            var node = new PoolNode { Name = "n" };
            node.Next = node; // cycle: forces reference tracking

            var first = mapper.Map<PoolNode, PoolNodeDto>(node);
            var second = mapper.Map<PoolNode, PoolNodeDto>(node);

            Assert.Same(first, first.Next);
            Assert.Same(second, second.Next);
            Assert.NotSame(first, second);
        }

        [Fact]
        public void Parallel_calls_with_different_items_stay_isolated()
        {
            var mapper = CreateMapper(pooling: true);
            var results = new PoolDest[2000];

            Parallel.For(0, results.Length, i =>
                results[i] = mapper.Map<PoolDest>(new PoolSource { Id = i }, opt => opt.Items["Tag"] = "t" + i));

            Assert.All(Enumerable.Range(0, results.Length), i =>
            {
                Assert.Equal(i, results[i].Id);
                Assert.Equal("t" + i, results[i].Tag);
            });
        }

        [Fact]
        public void A_failed_map_does_not_poison_the_pooled_context()
        {
            var mapper = new MapperConfiguration(cfg =>
                cfg.CreateMap<PoolSource, PoolDest>()
                   .ForMember(d => d.Tag, o => o.MapFrom((s, d, m, ctx) =>
                   {
                       if (s.Id < 0) throw new System.InvalidOperationException("boom");
                       return ctx.Items.TryGetValue("Tag", out var t) ? (string)t : null;
                   }))).CreateMapper();

            Assert.ThrowsAny<System.Exception>(() => mapper.Map<PoolDest>(new PoolSource { Id = -1 }, opt => opt.Items["Tag"] = "bad"));
            Assert.Null(mapper.Map<PoolSource, PoolDest>(new PoolSource { Id = 1 }).Tag);
        }
    }
}
