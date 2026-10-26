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
        public void Clone_equality_references_and_rows_check_their_signatures()
        {
            var result = GeneratorHarness.Generate(Models + @"
    public class Nested { public A Inner { get; set; } = new A(); }
    [GeneratedMapper] public static partial class M
    {
        [MapClone] public static partial B NotAClone(A a);
        [MapEquality] public static partial System.Collections.Generic.IEqualityComparer<A> Bad(A a);
        [MapPreserveReferences] public static partial void Update(A a, B b);
        public static partial Nested Read(System.Data.IDataRecord row);
    }
}");
            var messages = result.Generator.Select(d => d.Id + " " + d.GetMessage()).ToList();
            Assert.Contains(messages, m => m.StartsWith("CM0025") && m.Contains("a clone method must be T Clone(T value)"));
            Assert.Contains(messages, m => m.StartsWith("CM0025") && m.Contains("an equality method must be IEqualityComparer<T> Comparer()"));
            Assert.Contains(messages, m => m.StartsWith("CM0025") && m.Contains("only methods that create an object from an object can keep references"));
            Assert.Contains(messages, m => m.StartsWith("CM0002") && m.Contains("column 'Inner'"));
        }

        [Fact]
        public void Culture_sensitive_helpers_are_reported()
        {
            var result = GeneratorHarness.Generate(@"using ConventionMapper; using System; using System.Globalization;
namespace K
{
    public class Money { public decimal Amount { get; set; } public DateTime When { get; set; } }
    public class Row { public string Amount { get; set; } = """"; public decimal Price { get; set; } public string When { get; set; } = """"; }
    public class Text { public string Price { get; set; } = """"; }
    [GeneratedMapper] public static partial class M
    {
        public static partial Row ToRow(Money m);
        public static partial Row FromText(Text t);
        [MapConversion] private static string Format(decimal d) => d.ToString();
        [MapConversion] private static decimal Parse(string s) => decimal.Parse(s);
        [MapConversion] private static string Day(DateTime d) => $""{d:yyyy-MM-dd}"";
        private static string Fine(decimal d) => d.ToString(CultureInfo.InvariantCulture) + FormattableString.Invariant($""{d}"") + 1.ToString();
    }
}");
            var messages = result.WithId("CM0030").Select(d => d.GetMessage()).ToList();
            Assert.Equal(3, messages.Count);
            Assert.Contains(messages, m => m.StartsWith("'Format' uses decimal.ToString() without a format provider"));
            Assert.Contains(messages, m => m.StartsWith("'Parse' uses decimal.Parse without a format provider"));
            Assert.Contains(messages, m => m.StartsWith("'Day' uses an interpolated DateTime"));
        }

        [Fact]
        public void Obsolete_members_and_dropped_time_parts_are_reported()
        {
            var result = GeneratorHarness.Generate(@"using ConventionMapper; using System;
namespace K
{
    public class S { [Obsolete(""Use Code."")] public string Legacy { get; set; } = """"; public DateTime Day { get; set; } }
    public class D { public string Legacy { get; set; } = """"; [Obsolete] public string Other { get; set; } = """"; public DateOnly Day { get; set; } }
    [GeneratedMapper] public static partial class M { [MapMember(nameof(S.Legacy), nameof(D.Other))] public static partial D Map(S s); }
}");
            var obsolete = result.WithId("CM0031").Select(d => d.GetMessage()).ToList();
            Assert.Contains("'Map' reads 'S.Legacy', which is [Obsolete]: Use Code.", obsolete);
            Assert.Contains("'Map' writes 'D.Other', which is [Obsolete]", obsolete);
            Assert.Contains(result.WithId("CM0016"), d => d.GetMessage().Contains("dropping the time part (DateTime to DateOnly)"));
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
