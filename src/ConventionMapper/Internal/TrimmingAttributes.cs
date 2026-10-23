#if !NET8_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    // Polyfills so the netstandard2.0 build compiles; the trimmer and AOT analyzer only look at the net8.0 build.
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) { Message = message; }
        public string Message { get; }
        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) { Message = message; }
        public string Message { get; }
        public string? Url { get; set; }
    }
}
#endif

namespace ConventionMapper
{
    internal static class TrimMessages
    {
        public const string Message =
            "ConventionMapper discovers members with reflection and compiles mappings at runtime, which is not compatible with trimming or Native AOT.";
    }
}
