#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // Lets the tests use records and init accessors on .NET Framework 4.8.
    internal static class IsExternalInit { }
}
#endif
