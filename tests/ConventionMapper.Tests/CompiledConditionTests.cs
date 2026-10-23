using System.Collections.Generic;
using ConventionMapper;

namespace ConventionMapper.Tests
{
    public class CondSource { public int Age { get; set; } public string? Name { get; set; } public int? Score { get; set; } public string Email { get; set; } = ""; }
    public class CondDest { public int Age { get; set; } = -1; public string? Name { get; set; } = "keep"; public int? Score { get; set; } public string Email { get; set; } = "old"; }

    public class CompiledConditionTests
    {
        private static IMapper Mapper(bool compiled, List<string>? log = null) => new MapperConfiguration(cfg =>
        {
            cfg.UseCompiledMappings = compiled;
            cfg.CreateMap<CondSource, CondDest>()
               .ForMember(d => d.Age, o => o.Condition(s => s.Age >= 0))
               .ForMember(d => d.Name, o =>
               {
                   o.PreCondition(s => s.Age != 99);
                   o.NullSubstitute("n/a");
               })
               .ForMember(d => d.Score, o => o.NullSubstitute(0))
               .ForMember(d => d.Email, o => o.Condition((s, d, sourceMember, destMember, ctx) =>
               {
                   log?.Add(sourceMember + "<-" + destMember);
                   return sourceMember != "";
               }));
        }).CreateMapper();

        public static IEnumerable<object[]> Sources()
        {
            yield return new object[] { new CondSource { Age = 5, Name = "a", Score = 3, Email = "e" } };
            yield return new object[] { new CondSource { Age = -5, Name = null, Score = null, Email = "" } };
            yield return new object[] { new CondSource { Age = 99, Name = "skipped", Score = 1, Email = "x" } };
        }

        private static string Describe(CondDest d) => d.Age + "|" + (d.Name ?? "null") + "|" + (d.Score?.ToString() ?? "null") + "|" + d.Email;

        [Theory]
        [MemberData(nameof(Sources))]
        public void Compiled_conditions_match_the_interpreter(CondSource source)
        {
            var compiled = Mapper(true).Map<CondSource, CondDest>(source);
            var interpreted = Mapper(false).Map<CondSource, CondDest>(source);
            Assert.Equal(Describe(interpreted), Describe(compiled));

            var existingCompiled = Mapper(true).Map(source, new CondDest());
            var existingInterpreted = Mapper(false).Map(source, new CondDest());
            Assert.Equal(Describe(existingInterpreted), Describe(existingCompiled));
        }

        [Fact]
        public void Conditions_see_source_and_current_destination_values()
        {
            var compiledLog = new List<string>();
            var interpretedLog = new List<string>();
            Mapper(true, compiledLog).Map<CondSource, CondDest>(new CondSource { Email = "new" });
            Mapper(false, interpretedLog).Map<CondSource, CondDest>(new CondSource { Email = "new" });

            Assert.Equal(new[] { "new<-old" }, compiledLog);
            Assert.Equal(interpretedLog, compiledLog);
        }

        [Fact]
        public void Conditional_members_are_compiled_not_interpreted()
        {
            var config = new MapperConfiguration(cfg =>
                cfg.CreateMap<CondSource, CondDest>().ForMember(d => d.Age, o => o.Condition(s => s.Age >= 0)));
            var plan = config.FindTypeMapFor<CondSource, CondDest>()!.GetCompiledPlan(config.Engine.Compiler);
            Assert.NotNull(plan);
            Assert.Equal(7, config.CreateMapper().Map<CondSource, CondDest>(new CondSource { Age = 7 }).Age);
            Assert.Equal(-1, config.CreateMapper().Map<CondSource, CondDest>(new CondSource { Age = -7 }).Age);
        }
    }
}
