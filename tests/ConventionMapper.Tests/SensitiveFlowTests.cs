#if NET8_0_OR_GREATER
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.Tests
{
    /// <summary>CM0032: [Sensitive] data passed to loggers, exception messages and ToString().</summary>
    public class SensitiveFlowTests
    {
        private const string Logging = @"
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public static class LoggerExtensions
    {
        public static void LogInformation(this ILogger logger, string message, params object?[] args) { }
    }
}
";

        private const string Models = @"using ConventionMapper; using Microsoft.Extensions.Logging; using System.Collections.Generic;
namespace App
{
    public class Address { [Sensitive] public string Street { get; set; } = """"; public string City { get; set; } = """"; }
    public class User { public int Id { get; set; } [Sensitive] public string Ssn { get; set; } = """"; }
    public class Customer { public int Id { get; set; } public Address Home { get; set; } = new(); }
    public class Plain { public int Id { get; set; } }

    [GeneratedMapper]
    public static partial class Mappings
    {
        [MapRedact] public static partial User Redact(User user);
        [MapLogString] public static partial string ToLogString(User user);
    }
";

        private static async Task<ImmutableArray<Diagnostic>> Analyze(string code, DiagnosticAnalyzer analyzer, bool withGenerator = true)
        {
            Compilation compilation = GeneratorHarness.Compile(code + Logging);
            if (withGenerator)
            {
                var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: GeneratorHarness.ParseOptions);
                driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out _);
            }
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create(analyzer)).GetAnalyzerDiagnosticsAsync();
            var crash = diagnostics.FirstOrDefault(d => d.Id == "AD0001");
            Assert.True(crash == null, crash?.GetMessage());
            return diagnostics.Where(d => d.Id == "CM0032").ToImmutableArray();
        }

        [Fact]
        public async Task Objects_with_sensitive_members_are_reported_in_log_calls()
        {
            var diagnostics = await Analyze(Models + @"
    public static class Service
    {
        public static void Run(ILogger log, User user, Customer customer, Plain plain, List<User> users)
        {
            log.LogInformation(""Saved {User}"", user);                       // reported
            log.LogInformation($""Saved {customer}"");                         // reported: nested Address.Street
            log.LogInformation(""Saved {Users}"", users);                      // reported: collection of users
            log.LogInformation(""Saved {User}"", Mappings.Redact(user));       // fine: redacted
            log.LogInformation(""Saved {User}"", Mappings.ToLogString(user));  // fine: masked text
            log.LogInformation(""Saved {Plain} {Id}"", plain, user.Id);         // fine
        }
    }
}", new SensitiveFlowAnalyzer());

            var messages = diagnostics.Select(d => d.GetMessage()).ToList();
            Assert.Equal(3, messages.Count);
            Assert.Contains("'user' (User) has the [Sensitive] member User.Ssn and is written to the log by LogInformation(); log a [MapLogString] or [MapRedact] result instead", messages);
            Assert.Contains(messages, m => m.StartsWith("'customer' (Customer) has the [Sensitive] member Address.Street"));
            Assert.Contains(messages, m => m.StartsWith("'users' (List<User>) has the [Sensitive] member User.Ssn"));
            Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        }

        [Fact]
        public async Task Exception_messages_and_ToString_are_reported()
        {
            var diagnostics = await Analyze(Models + @"
    public class Account
    {
        [Sensitive] public string Token { get; set; } = """";
        public string Name { get; set; } = """";
        public override string ToString() => Name + "" "" + Token;
    }
    public record Login(string Name, [property: Sensitive] string Password);
    public record SafeLogin(string Name, [property: Sensitive] string Password)
    {
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) { builder.Append(""Name = "").Append(Name); return true; }
    }
    public static class Service
    {
        public static void Fail(User user) => throw new System.InvalidOperationException(""Cannot save "" + user);
        public static void Fine(User user) => throw new System.InvalidOperationException(""Cannot save "" + user.Id);
    }
}", new SensitiveFlowAnalyzer());

            var messages = diagnostics.Select(d => d.GetMessage()).ToList();
            Assert.Equal(3, messages.Count);
            Assert.Contains(messages, m => m.StartsWith("ToString() of 'Account' writes the [Sensitive] member 'Account.Token'"));
            Assert.Contains(messages, m => m.StartsWith("Record 'Login' prints all its public properties in ToString(), including the [Sensitive] 'Password'"));
            Assert.Contains(messages, m => m.StartsWith("'user' (User) has the [Sensitive] member User.Ssn and goes into the message of InvalidOperationException"));
        }

        [Fact]
        public async Task The_runtime_package_copy_reports_only_without_the_generator()
        {
            const string runtimeOnly = @"
namespace ConventionMapper { public sealed class SensitiveAttribute : System.Attribute { } }
namespace App
{
    using Microsoft.Extensions.Logging;
    public class User { [ConventionMapper.Sensitive] public string Ssn { get; set; } = """"; }
    public static class Service { public static void Run(ILogger log, User user) => log.LogInformation(""{User}"", user); }
}";
            Assert.Single(await Analyze(runtimeOnly, new ConventionMapper.Analyzers.SensitiveFlowAnalyzer(), withGenerator: false));
            Assert.Empty(await Analyze(Models + "public static class S { public static void Run(ILogger log, User u) => log.LogInformation(\"{U}\", u); } }",
                new ConventionMapper.Analyzers.SensitiveFlowAnalyzer()));
        }
    }
}
#endif
