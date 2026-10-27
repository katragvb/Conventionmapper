#if NET8_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;

// The runtime mapper discovers members with reflection and compiles mappings at runtime. Every public API needs a
// MapperConfiguration (or AddConventionMapper), and those entry points carry [RequiresUnreferencedCode] and
// [RequiresDynamicCode], so trimmed and Native AOT apps get one warning where they create the configuration, not one per
// internal reflection call. Apps that must trim or compile ahead of time use ConventionMapper.SourceGenerator instead.
[module: UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reached only through annotated entry points.", Scope = "module")]
[module: UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reached only through annotated entry points.", Scope = "module")]
#endif
