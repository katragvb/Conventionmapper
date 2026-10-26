using System;
using Microsoft.CodeAnalysis;

namespace ConventionMapper.SourceGenerator
{
    /// <summary>
    /// The LINQ provider named by [GeneratedMapper(Provider = ...)], and what each one translates.
    /// The generated expressions are the same for every provider ("??" becomes COALESCE, enum conditionals CASE WHEN);
    /// the provider only decides which parts of a projection are warned about (CM0021).
    /// </summary>
    internal static class ProjectionProvider
    {
        public const string Default = "Default";
        public const string SqlServer = "SqlServer";
        public const string PostgreSql = "PostgreSql";
        public const string Sqlite = "Sqlite";
        public const string Cosmos = "Cosmos";
        public const string InMemory = "InMemory";

        public const string KnownList = "SqlServer, PostgreSql, Sqlite, Cosmos, InMemory";

        public static string? Normalize(string value)
        {
            foreach (var known in new[] { SqlServer, PostgreSql, Sqlite, Cosmos, InMemory, Default })
                if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase)) return known;
            if (string.Equals(value, "Postgres", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Npgsql", StringComparison.OrdinalIgnoreCase))
                return PostgreSql;
            return null;
        }

        /// <summary>The query runs in .NET, so nothing has to translate.</summary>
        public static bool RunsInMemory(string provider) => provider == InMemory;

        /// <summary>
        /// Why <c>value.ToString()</c> in a projection is a problem for this provider, or null when it translates and formats
        /// like .NET. Relational providers translate integers, Guid and char reliably; dates, floating-point numbers, decimals,
        /// booleans and enums are formatted by the database, which differs from .NET (or, for older EF Core versions, fail).
        /// Cosmos translates only a few conversions.
        /// </summary>
        public static string? ToStringProblem(string provider, ITypeSymbol type)
        {
            if (RunsInMemory(provider)) return null;
            if (provider == Cosmos) return "may not translate on Cosmos DB";
            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return null;
            }
            if (type.ToDisplayString() == "System.Guid") return null;
            return "is formatted by the database, differently from .NET (or does not translate on older EF Core versions)";
        }
    }
}
