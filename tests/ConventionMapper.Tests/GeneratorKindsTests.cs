#if NET8_0_OR_GREATER
using System.Linq;

namespace ConventionMapper.Tests
{
    /// <summary>Misuse of [MapPatch], [MapCombine], [MapPrefer] and [MapWhen] is reported, not guessed.</summary>
    public class GeneratorKindsTests
    {
        private const string Models = @"using ConventionMapper; using System.Linq;
namespace K
{
    public class A { public string? Name { get; set; } public int Age { get; set; } }
    public class B { public string? Name { get; set; } public int Age { get; set; } }
";

        [Fact]
        public void Patch_on_a_create_method_is_an_error()
        {
            var result = GeneratorHarness.Generate(Models + @"
    [GeneratedMapper] public static partial class M { [MapPatch] public static partial B ToB(A a); }
}");
            var error = Assert.Single(result.WithId("CM0025"));
            Assert.Contains("[MapPatch]", error.GetMessage());
            Assert.Contains("a patch updates an existing object", error.GetMessage());
        }

        [Fact]
        public void Patch_guards_nullable_sources_in_the_generated_code()
        {
            var result = GeneratorHarness.Generate(Models + @"
    [GeneratedMapper] public static partial class M { [MapPatch] public static partial void Patch(A a, B b); }
}");
            Assert.Empty(result.CompileErrors);
            Assert.Contains("if ((a.Name) is { } ", result.Source);
            Assert.Contains("b.Age = a.Age;", result.Source); // non-nullable: always
        }

        [Fact]
        public void Combine_needs_two_sources_and_a_known_preferred_parameter()
        {
            var one = GeneratorHarness.Generate(Models + @"
    [GeneratedMapper] public static partial class M { [MapCombine] public static partial B C(A a); }
}");
            Assert.Contains(one.WithId("CM0025"), d => d.GetMessage().Contains("two or more source objects"));

            var prefer = GeneratorHarness.Generate(Models + @"
    [GeneratedMapper] public static partial class M { [MapCombine, MapPrefer(""other"", ""Name""), MapPrefer(""second"", ""Nam"")] public static partial B C(A first, A second); }
}");
            Assert.Contains(prefer.WithId("CM0025"), d => d.GetMessage().Contains("'other' is not a parameter of C"));
            Assert.Contains(prefer.WithId("CM0008"), d => d.GetMessage().Contains("'Nam' in [MapPrefer]"));
            Assert.Empty(prefer.CompileErrors);
        }

        [Fact]
        public void When_needs_a_static_bool_method()
        {
            var result = GeneratorHarness.Generate(Models + @"
    [GeneratedMapper] public static partial class M
    {
        [MapWhen(""Name"", ""Check"")] public static partial B ToB(A a);
        [MapWhen(""Nme"", nameof(Ok))] public static partial B ToB2(A a);
        private static int Check(A a) => 1;
        private static bool Ok(A a) => true;
    }
}");
            Assert.Contains(result.WithId("CM0025"), d => d.GetMessage().Contains("needs a static bool method 'Check'"));
            Assert.Contains(result.WithId("CM0008"), d => d.GetMessage().Contains("'Nme' in [MapWhen]"));
        }
    }
}
#endif
