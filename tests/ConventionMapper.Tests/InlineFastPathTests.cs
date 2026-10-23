using System;
using System.Collections.Generic;
using System.Linq;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class InItem { public string Sku { get; set; } = ""; public int Qty { get; set; } public InInfo? Info { get; set; } }
    public class InInfo { public string Note { get; set; } = ""; }
    public class InFailingItem
    {
        public bool Fail { get; set; }
        public string Sku { get; set; } = "";
        public int Qty => Fail ? throw new InvalidOperationException("boom") : 1;
    }

    public class InSource
    {
        public int Id { get; set; }
        public InItem? Main { get; set; }
        public List<InItem>? Items { get; set; }
        public InItem[] ArrayItems { get; set; } = Array.Empty<InItem>();
        public HashSet<int> Codes { get; set; } = new HashSet<int>();
        public IEnumerable<InItem> Lazy { get; set; } = Enumerable.Empty<InItem>();
        public List<InFailingItem> Failing { get; set; } = new List<InFailingItem>();
    }

    public class InItemDto { public string Sku { get; set; } = ""; public int Qty { get; set; } public InInfoDto? Info { get; set; } }
    public class InInfoDto { public string Note { get; set; } = ""; }
    public class InFailingItemDto { public string Sku { get; set; } = ""; public int Qty { get; set; } }

    public class InDest
    {
        public int Id { get; set; }
        public InItemDto? Main { get; set; } = new InItemDto { Sku = "initial" };
        public List<InItemDto> Items { get; set; } = new List<InItemDto>();
        public IReadOnlyList<InItemDto> ArrayItems { get; set; } = new List<InItemDto>();
        public int[] Codes { get; set; } = Array.Empty<int>();
        public InItemDto[] Lazy { get; set; } = Array.Empty<InItemDto>();
        public List<InFailingItemDto> Failing { get; set; } = new List<InFailingItemDto>();
    }

    public class InlineFastPathTests
    {
        private static MapperConfiguration Config(bool compiled, bool allowNullCollections = false) => new MapperConfiguration(cfg =>
        {
            cfg.UseCompiledMappings = compiled;
            cfg.AllowNullCollections = allowNullCollections;
            cfg.CreateMap<InSource, InDest>();
            cfg.CreateMap<InItem, InItemDto>();
            cfg.CreateMap<InInfo, InInfoDto>();
            cfg.CreateMap<InFailingItem, InFailingItemDto>();
        });

        private static InSource Sample() => new InSource
        {
            Id = 7,
            Main = new InItem { Sku = "main", Qty = 1, Info = new InInfo { Note = "n" } },
            Items = new List<InItem> { new InItem { Sku = "a", Qty = 1 }, null!, new InItem { Sku = "c", Qty = 3, Info = new InInfo { Note = "deep" } } },
            ArrayItems = new[] { new InItem { Sku = "x" } },
            Codes = new HashSet<int> { 4, 5 },
            Lazy = Enumerable.Range(1, 3).Select(i => new InItem { Sku = "l" + i }),
            Failing = new List<InFailingItem> { new InFailingItem { Sku = "ok" } }
        };

        private static string Describe(InDest d) =>
            string.Join("|",
                d.Id,
                d.Main == null ? "null" : d.Main.Sku + ":" + d.Main.Info?.Note,
                string.Join(",", d.Items.Select(i => i == null ? "null" : i.Sku + ":" + i.Qty + ":" + i.Info?.Note)),
                string.Join(",", d.ArrayItems.Select(i => i.Sku)),
                string.Join(",", d.Codes),
                string.Join(",", d.Lazy.Select(i => i.Sku)),
                string.Join(",", d.Failing.Select(i => i.Sku + ":" + i.Qty)));

        [Fact]
        public void The_fast_path_uses_the_inlined_delegate()
        {
            var config = Config(compiled: true);
            var plan = config.FindTypeMapFor<InSource, InDest>()!.GetCompiledPlan(config.Engine.Compiler);
            Assert.NotNull(plan);
            Assert.True(plan!.ContextFree);
            Assert.NotNull(plan.FastMap);
            Assert.IsType<Func<InSource, InDest>>(plan.TypedFastMap);
        }

        [Fact]
        public void Inlined_results_match_the_interpreter()
        {
            var fast = Config(compiled: true).CreateMapper().Map<InSource, InDest>(Sample());
            var interpreted = Config(compiled: false).CreateMapper().Map<InSource, InDest>(Sample());

            Assert.Equal(Describe(interpreted), Describe(fast));
            Assert.Equal("main:n|", Describe(fast).Split('|')[1] + "|");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Null_collections_and_objects_match_the_interpreter(bool allowNullCollections)
        {
            var source = new InSource { Id = 1, Main = null, Items = null };
            var fast = Config(true, allowNullCollections).CreateMapper().Map<InSource, InDest>(source);
            var interpreted = Config(false, allowNullCollections).CreateMapper().Map<InSource, InDest>(source);

            Assert.Null(fast.Main);
            Assert.Equal(interpreted.Items == null, fast.Items == null);
            if (!allowNullCollections) Assert.Empty(fast.Items);
        }

        [Fact]
        public void Existing_destination_objects_are_reused()
        {
            var fast = Config(compiled: true).CreateMapper().Map<InSource, InDest>(Sample());
            var interpreted = Config(compiled: false).CreateMapper().Map<InSource, InDest>(Sample());

            // The initializer-created Main object is updated in place by both paths.
            Assert.Equal(interpreted.Main!.Sku, fast.Main!.Sku);
        }

        [Fact]
        public void Errors_inside_inlined_collections_keep_the_element_path()
        {
            var source = Sample();
            source.Failing = new List<InFailingItem> { new InFailingItem { Sku = "ok" }, new InFailingItem { Sku = "bad", Fail = true } };
            var mapper = Config(compiled: true).CreateMapper();

            var ex = Assert.Throws<MappingException>(() => mapper.Map<InSource, InDest>(source));

            Assert.Contains("Failing[1]", ex.MemberPath);
        }

        [Fact]
        public void Repeated_calls_are_stable()
        {
            var mapper = Config(compiled: true).CreateMapper();
            var first = Describe(mapper.Map<InSource, InDest>(Sample()));
            for (var i = 0; i < 5; i++) Assert.Equal(first, Describe(mapper.Map<InSource, InDest>(Sample())));
            Assert.Equal(first, Describe(mapper.Map<InDest>(Sample())));
        }
    }
}
