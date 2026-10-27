#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ConventionMapper.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConventionMapper.Tests
{
    /// <summary>Every diagnostic links to its own section of docs/diagnostics.md, and the section exists.</summary>
    public class DiagnosticDocsTests
    {
        private static IEnumerable<DiagnosticDescriptor> AllDescriptors()
        {
            var assemblies = new[] { typeof(MapperGenerator).Assembly, typeof(ConventionMapper.Analyzers.SensitiveFlowAnalyzer).Assembly };
            foreach (var assembly in assemblies)
                foreach (var type in assembly.GetTypes())
                {
                    foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        if (field.FieldType == typeof(DiagnosticDescriptor) && field.GetValue(null) is DiagnosticDescriptor descriptor)
                            yield return descriptor;
                    if (typeof(DiagnosticAnalyzer).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(System.Type.EmptyTypes) != null)
                        foreach (var descriptor in ((DiagnosticAnalyzer)System.Activator.CreateInstance(type)!).SupportedDiagnostics)
                            yield return descriptor;
                }
        }

        [Fact]
        public void Every_diagnostic_links_to_its_documented_section()
        {
            var docs = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "diagnostics.md"));
            var descriptors = AllDescriptors().GroupBy(d => d.Id + "|" + d.DefaultSeverity).Select(g => g.First()).ToList();
            Assert.True(descriptors.Count >= 35, "found only " + descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                Assert.Equal("https://github.com/katragvb/Conventionmapper/blob/main/docs/diagnostics.md#" + descriptor.Id.ToLowerInvariant(), descriptor.HelpLinkUri);
                Assert.Contains("\n## " + descriptor.Id + "\n", docs.Replace("\r\n", "\n"));
            }
        }

        /// <summary>The repository root: from this file's path, or (when CI maps source paths) up from the test binaries.</summary>
        private static string RepoRoot([CallerFilePath] string path = "")
        {
            var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
            if (File.Exists(Path.Combine(fromSource, "docs", "diagnostics.md"))) return fromSource;
            for (var dir = new DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "docs", "diagnostics.md"))) return dir.FullName;
            throw new DirectoryNotFoundException("docs/diagnostics.md not found");
        }
    }
}
#endif
