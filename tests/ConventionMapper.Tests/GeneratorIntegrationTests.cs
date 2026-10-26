#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Linq;

namespace ConventionMapper.Tests
{
    /// <summary>Coverage attributes, [MapUse], read-only collections and projection expression properties.</summary>
    public class GeneratorIntegrationTests
    {
        private const string Code = @"using ConventionMapper; using System.Collections.Generic; using System.Linq;
namespace I
{
    public class Order { public int Id { get; set; } public List<string> Tags { get; set; } = new(); }
    public class OrderDto { public int Id { get; set; } public List<string> Tags { get; } = new(); }
    [GeneratedMapper] public static partial class M
    {
        public static partial OrderDto ToDto(Order o);
        public static partial IQueryable<OrderDto> Project(IQueryable<Order> q);
    }
}";

        [Fact]
        public void Generated_members_are_excluded_from_code_coverage_unless_turned_off()
        {
            var result = GeneratorHarness.Generate(Code);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("[global::System.CodeDom.Compiler.GeneratedCode(\"ConventionMapper.SourceGenerator\", ", result.Source);
            Assert.Contains(")][global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]", result.Source);

            var off = GeneratorHarness.Generate(Code, new Dictionary<string, string> { ["ConventionMapperExcludeFromCodeCoverage"] = "false" });
            Assert.DoesNotContain("ExcludeFromCodeCoverage", off.Source);
            Assert.Contains("GeneratedCode(", off.Source);
        }

        [Fact]
        public void Read_only_collections_are_filled_and_projections_expose_their_expression()
        {
            var result = GeneratorHarness.Generate(Code);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("&& __result.Tags is { } __d) { foreach (var item in ", result.Source);
            Assert.Contains("public static global::System.Linq.Expressions.Expression<global::System.Func<global::I.Order, global::I.OrderDto>> ProjectExpression =>", result.Source);
            Assert.Contains("| Tags | `Tags` | added to the existing collection |", result.Report);
        }

        [Fact]
        public void MapUse_reuses_mapping_methods_of_another_class()
        {
            var result = GeneratorHarness.Generate(@"using ConventionMapper;
namespace Shared { public static class SharedMappings { public static B.Money Convert(A.Price p) => new B.Money { Cents = p.Cents }; } }
namespace A { public class Price { public int Cents { get; set; } } public class Item { public Price Price { get; set; } = new(); } }
namespace B { public class Money { public int Cents { get; set; } } public class ItemDto { public Money Price { get; set; } = new(); }
    [GeneratedMapper, MapUse(typeof(Shared.SharedMappings))] public static partial class M { public static partial ItemDto Map(A.Item i); }
    [GeneratedMapper, MapUse(typeof(M))] public static partial class Empty { public static partial ItemDto Map2(A.Item i); }
}");
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Price = global::Shared.SharedMappings.Convert(i.Price)", result.Source);
            Assert.Empty(result.WithId("CM0017"));
        }
    }
}
#endif
