using System.Collections.Immutable;
using ConventionMapper.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.Analyzers
{
    /// <summary>
    /// CM0032 for projects that use only the runtime package: [Sensitive] data passed to a logger, an exception message or
    /// ToString(). When ConventionMapper.SourceGenerator is also referenced, its copy of this analyzer reports instead.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SensitiveFlowAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(SensitiveFlow.Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            SensitiveFlow.Register(context, deferToGenerator: true);
        }
    }
}
