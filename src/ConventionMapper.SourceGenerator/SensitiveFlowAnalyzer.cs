using System.Collections.Immutable;
using ConventionMapper.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>CM0032: [Sensitive] data passed to a logger, an exception message or ToString() (see <see cref="SensitiveFlow"/>).</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SensitiveFlowAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(SensitiveFlow.Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            SensitiveFlow.Register(context, deferToGenerator: false);
        }
    }
}
