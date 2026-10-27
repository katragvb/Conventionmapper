using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ConventionMapper.Testing
{
    public static partial class MapperAssert
    {
        /// <summary>The default folder for generated-code snapshots, next to the calling test file.</summary>
        public const string GeneratedCodeFolder = "generated-code";

        /// <summary>
        /// Snapshots the generated code of one mapping method in its own file (<c>generated-code/Mappings.ToDto(Order).cs</c>),
        /// so a library upgrade or attribute change that alters that method's generated body fails a test that names it.
        /// A missing file is created and the check fails once; run the tests with CONVENTIONMAPPER_UPDATE_SNAPSHOTS=1 to accept
        /// changes. Needs <c>&lt;ConventionMapperEmbedGeneratedCode&gt;true&lt;/ConventionMapperEmbedGeneratedCode&gt;</c> in the
        /// project that contains the mapper.
        /// </summary>
        /// <param name="mapperType">The [GeneratedMapper] class.</param>
        /// <param name="method">The method name, preferably <c>nameof(Mappings.ToDto)</c>.</param>
        /// <param name="parameterTypes">The parameter types, needed when the method has overloads.</param>
        /// <param name="folder">Snapshot folder; a relative path starts at the folder of the calling source file.</param>
        /// <param name="callerFilePath">Filled in by the compiler.</param>
        public static void GeneratedCodeMatches(Type mapperType, string method, Type[]? parameterTypes = null, string folder = GeneratedCodeFolder,
            [CallerFilePath] string callerFilePath = "")
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("A method name is required.", nameof(method));
            var code = GeneratedCodeOf(mapperType, method, parameterTypes ?? Type.EmptyTypes);
            var key = mapperType.Name + "." + (parameterTypes == null || parameterTypes.Length == 0 ? method : method + "(" + string.Join(", ", parameterTypes.Select(t => t.Name)) + ")");
            var failures = new List<string>();
            Snapshots.Check(Path.Combine(Snapshots.Resolve(folder, callerFilePath), Snapshots.FileName(key, ".cs")), code, "generated code of " + key, failures);
            if (failures.Count > 0) throw new MapperAssertException(string.Join("\n\n", failures));
        }

        /// <summary>
        /// Snapshots every generated method of <paramref name="mapperType"/>, one file per method in
        /// <c>folder/MapperName/</c>; files for methods that no longer exist fail as stale (and are deleted when updating).
        /// </summary>
        public static void GeneratedCodeMatchesDirectory(Type mapperType, string folder = GeneratedCodeFolder, [CallerFilePath] string callerFilePath = "")
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            var all = EmbeddedCode(mapperType);
            var directory = Path.Combine(Snapshots.Resolve(folder, callerFilePath), mapperType.Name);
            var failures = new List<string>();
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Keys with parameter types are unique; the bare-name duplicates of non-overloaded methods are skipped.
            foreach (var pair in all.Where(p => p.Key.IndexOf('(') >= 0).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var name = Snapshots.FileName(pair.Key, ".cs");
                expected.Add(name);
                Snapshots.Check(Path.Combine(directory, name), pair.Value, "generated code of " + mapperType.Name + "." + pair.Key, failures);
            }
            if (Directory.Exists(directory))
                foreach (var file in Directory.GetFiles(directory, "*.cs"))
                {
                    if (expected.Contains(Path.GetFileName(file))) continue;
                    if (Snapshots.Updating) File.Delete(file);
                    else failures.Add("Stale generated-code snapshot: " + file + " belongs to no method of " + mapperType.Name + ". Delete it, or run the tests with " + Snapshots.UpdateVariable + "=1.");
                }
            if (failures.Count > 0) throw new MapperAssertException(string.Join("\n\n", failures));
        }

        private static string GeneratedCodeOf(Type mapperType, string method, Type[] parameterTypes)
        {
            var accessor = mapperType.GetMethod("GeneratedCodeOf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Type[]) }, null);
            if (accessor == null) throw new MapperAssertException(NoEmbeddedCode(mapperType));
            try
            {
                return (string)accessor.Invoke(null, new object[] { method, parameterTypes })!;
            }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException inner)
            {
                throw new MapperAssertException(inner.Message);
            }
        }

        private static IReadOnlyDictionary<string, string> EmbeddedCode(Type mapperType)
        {
            var field = mapperType.GetField("GeneratedCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is IReadOnlyDictionary<string, string> code) return code;
            throw new MapperAssertException(NoEmbeddedCode(mapperType));
        }

        private static string NoEmbeddedCode(Type mapperType) =>
            "'" + mapperType.FullName + "' has no embedded generated code. Add <ConventionMapperEmbedGeneratedCode>true</ConventionMapperEmbedGeneratedCode> " +
            "to the project that contains it (ConventionMapper.SourceGenerator 1.9 or later).";
    }
}
