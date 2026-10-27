using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace ConventionMapper.Testing
{
    public static partial class MapperAssert
    {
        // ------------------------------------------------------------------ lineage

        /// <summary>
        /// Checks where values come from, not only which members are written: maps a filled source (every value is distinct
        /// and strings carry the member name) and checks that each destination member holds the value of the expected source
        /// member path. A swapped pair (<c>SubmitUserId</c> filled from <c>CompleteUser.Id</c>) fails and names the member the
        /// value really came from. Pass the generated lineage to turn the data-flow report into a test:
        /// <c>MapperAssert.WritesFrom((Order o) =&gt; o.ToDto(), MapperAssert.LineageOf(typeof(Mappings), nameof(Mappings.ToDto)))</c>.
        /// Nested objects mapped by another method are left to that method's own check.
        /// </summary>
        public static void WritesFrom<TSource, TDestination>(Func<TSource, TDestination> map, IReadOnlyDictionary<string, string> expected, SampleDataOptions? options = null)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (options?.Seed != null) throw new ArgumentException("WritesFrom needs distinct values; use options without Randomized.", nameof(options));
            var source = SampleData.Create<TSource>(options);
            if (source == null) throw new MapperAssertException("SampleData cannot build a " + typeof(TSource).Name + "; add a factory with SampleDataOptions.Construct.");
            var result = map(source);
            if (result == null) throw new MapperAssertException("The mapping returned null for a filled " + typeof(TSource).Name + ".");
            var problems = LineageProblems(source, result, expected);
            if (problems.Count > 0)
                throw new MapperAssertException("Mapping " + typeof(TSource).Name + " → " + result.GetType().Name + " copies values from other members than expected:\n" +
                                                string.Join("\n", problems.Select(p => "  " + p)));
        }

        /// <summary>Same as <see cref="WritesFrom{TSource, TDestination}(Func{TSource, TDestination}, IReadOnlyDictionary{string, string}, SampleDataOptions)"/>, with (destination, source path) pairs inline.</summary>
        public static void WritesFrom<TSource, TDestination>(Func<TSource, TDestination> map, params (string Destination, string Source)[] expected) =>
            WritesFrom(map, (expected ?? throw new ArgumentNullException(nameof(expected))).ToDictionary(e => e.Destination, e => e.Source, StringComparer.Ordinal));

        /// <summary>
        /// The generated lineage of a mapping method (destination member → source path), through the mapper's generated
        /// <c>LineageOf</c>: <c>MapperAssert.LineageOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Order))</c>.
        /// </summary>
        public static IReadOnlyDictionary<string, string> LineageOf(Type mapperType, string method, params Type[] parameterTypes)
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            var accessor = mapperType.GetMethod("LineageOf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Type[]) }, null);
            if (accessor == null)
                throw new MapperAssertException("'" + mapperType.FullName + "' has no generated LineageOf. Is it a [GeneratedMapper] class built with ConventionMapper.SourceGenerator 1.9 or later?");
            try
            {
                return (IReadOnlyDictionary<string, string>)accessor.Invoke(null, new object[] { method, parameterTypes ?? Type.EmptyTypes })!;
            }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException inner)
            {
                throw new MapperAssertException(inner.Message);
            }
        }

        private static List<string> LineageProblems(object source, object result, IReadOnlyDictionary<string, string> expected)
        {
            var problems = new List<string>();
            var members = Members(result.GetType()).ToDictionary(m => m.Name, StringComparer.Ordinal);
            Dictionary<string, object?>? leaves = null;
            foreach (var pair in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!members.TryGetValue(pair.Key, out var member))
                {
                    problems.Add(pair.Key + ": " + result.GetType().Name + " has no such member");
                    continue;
                }
                var actual = member.Get(result);
                if (!TryResolve(source, pair.Value, out var wanted))
                {
                    problems.Add(pair.Key + ": " + source.GetType().Name + " has no member path '" + pair.Value + "'");
                    continue;
                }
                if (Matches(wanted, actual)) continue;
                // A nested object mapped by another method: that method's own checks cover it.
                if (!IsScalar(actual) && !IsScalar(wanted)) continue;

                leaves ??= Leaves(source);
                var from = Distinctive(actual)
                    ? leaves.Where(l => l.Key != pair.Value && Matches(l.Value, actual)).Select(l => l.Key).FirstOrDefault()
                    : null;
                if (from != null)
                    problems.Add(pair.Key + ": expected the value of " + pair.Value + " (" + Show(wanted) + "), but it holds " + Show(actual) + ", the value of " + from);
                else if (IsDefault(actual) && !IsDefault(wanted))
                    problems.Add(pair.Key + ": expected the value of " + pair.Value + " (" + Show(wanted) + "), but it is " + Show(actual));
                // Otherwise a conversion this check cannot follow (a formatted date, say): not reported.
            }
            return problems;
        }

        /// <summary>"Address.City" on an object; a null on the way gives null (generated maps use ?.).</summary>
        private static bool TryResolve(object root, string path, out object? value)
        {
            value = root;
            foreach (var name in path.Split('.'))
            {
                if (value == null) return true;
                var type = value.GetType();
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(value);
                    continue;
                }
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) return false;
                value = field.GetValue(value);
            }
            return true;
        }

        /// <summary>Every scalar member path of a sample source with its value (for "the value came from X instead").</summary>
        private static Dictionary<string, object?> Leaves(object root)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            void Walk(object? value, string prefix, int depth)
            {
                if (value == null || depth > SampleData.MaxDepth) return;
                foreach (var member in Members(value.GetType()))
                {
                    object? item;
                    try { item = member.Get(value); }
                    catch (TargetInvocationException) { continue; }
                    var path = prefix.Length == 0 ? member.Name : prefix + "." + member.Name;
                    if (IsScalar(item)) result[path] = item;
                    else if (!(item is IEnumerable)) Walk(item, path, depth + 1);
                }
            }
            Walk(root, string.Empty, 0);
            return result;
        }

        private static bool IsScalar(object? value)
        {
            if (value == null) return true;
            var type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is DateTime || value is DateTimeOffset ||
                   value is TimeSpan || value is Guid || value is Uri || type.FullName == "System.DateOnly" || type.FullName == "System.TimeOnly";
        }

        /// <summary>Values SampleData makes unique per member (not bools, chars or enums, which repeat).</summary>
        private static bool Distinctive(object? value) => value != null && !(value is bool) && !(value is char) && !value.GetType().IsEnum;

        private static bool IsDefault(object? value) =>
            value == null || value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType())) || value is string s && s.Length == 0;

        /// <summary>Equal, or equal after the built-in conversions a map applies (numbers, ToString, enum names and values).</summary>
        private static bool Matches(object? source, object? destination)
        {
            if (Same(source, destination)) return true;
            if (source == null || destination == null) return false;
            if (IsNumber(source) && IsNumber(destination))
            {
                try { return Convert.ToDecimal(source, CultureInfo.InvariantCulture) == Convert.ToDecimal(destination, CultureInfo.InvariantCulture); }
                catch (OverflowException) { return Convert.ToDouble(source, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(destination, CultureInfo.InvariantCulture)); }
            }
            if (destination is string text && !(source is string)) return string.Equals(Convert.ToString(source, CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
            if (source is string sourceText && !(destination is string)) return string.Equals(Convert.ToString(destination, CultureInfo.InvariantCulture), sourceText, StringComparison.Ordinal);
            if (source.GetType().IsEnum && IsNumber(destination) || destination.GetType().IsEnum && IsNumber(source))
                return Convert.ToInt64(source, CultureInfo.InvariantCulture) == Convert.ToInt64(destination, CultureInfo.InvariantCulture);
            if (source.GetType().IsEnum && destination.GetType().IsEnum) return source.ToString() == destination.ToString();
            return false;
        }

        private static bool IsNumber(object value) =>
            value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal;

        private static string Show(object? value) =>
            value == null ? "null" : value is string s ? "\"" + (s.Length > 40 ? s.Substring(0, 40) + "…" : s) + "\"" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name;

        // ------------------------------------------------------------------ baseline for every method

        /// <summary>
        /// A zero-effort baseline: runs every generated create and update method of <paramref name="mapperType"/> with
        /// sample data (extra parameters filled too) and checks it against the generator's own WriteSets and Lineage: it must
        /// not throw, must write no member outside its write set, must write the members it copies from a filled source, and
        /// must copy each from the right source member. A new map gets this coverage before anyone writes a dedicated test.
        /// Projections and collection merges are skipped (they need a query provider or a collection to merge into).
        /// </summary>
        public static void AllMappersMatchWriteSets(Type mapperType, SampleDataOptions? options = null)
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            if (options?.Seed != null) throw new ArgumentException("AllMappersMatchWriteSets needs distinct values; use options without Randomized.", nameof(options));
            var problems = new List<string>();
            var checkedCount = 0;
            foreach (var method in MappingMethods(mapperType).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (IsProjection(method) || method.IsGenericMethodDefinition) continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0) continue;
                var types = parameters.Select(p => p.ParameterType).ToArray();
                IReadOnlyList<string> writeSet;
                IReadOnlyDictionary<string, string>? lineage = null;
                try
                {
                    writeSet = WriteSetOf(mapperType, method.Name, types);
                }
                catch (MapperAssertException)
                {
                    continue; // not a generated mapping method (a hand-written helper with the same name)
                }
                try { lineage = LineageOf(mapperType, method.Name, types); }
                catch (MapperAssertException) { }

                object? instance = null;
                if (!method.IsStatic && (instance = TryCreateEmpty(mapperType)) == null) continue;

                var isUpdate = method.ReturnType == typeof(void) && parameters.Length >= 2 && !parameters[1].ParameterType.IsValueType &&
                               !(parameters[1].ParameterType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(parameters[1].ParameterType));
                if (method.ReturnType == typeof(void) && !isUpdate) continue;

                var source = SampleData.Create(parameters[0].ParameterType, options);
                if (source == null) continue;
                var args = new object?[parameters.Length];
                args[0] = source;
                object? target = null;
                Dictionary<string, object?>? before = null;
                if (isUpdate)
                {
                    target = TryCreateEmpty(parameters[1].ParameterType);
                    if (target == null) continue;
                    args[1] = target;
                    before = Snapshot(target, target.GetType());
                }
                for (var i = isUpdate ? 2 : 1; i < parameters.Length; i++) args[i] = SampleData.CreateParameter(parameters[i], options);

                checkedCount++;
                var name = Describe(method);
                object? result;
                try
                {
                    result = method.Invoke(instance, args);
                }
                catch (TargetInvocationException e)
                {
                    problems.Add(name + ": threw " + e.InnerException!.GetType().Name + ": " + e.InnerException.Message);
                    continue;
                }

                List<string> written;
                object destination;
                if (isUpdate)
                {
                    destination = target!;
                    var after = Snapshot(destination, destination.GetType());
                    written = before!.Keys.Where(k => !Same(before[k], after[k])).ToList();
                }
                else
                {
                    if (result == null)
                    {
                        problems.Add(name + ": returned null for a filled " + parameters[0].ParameterType.Name);
                        continue;
                    }
                    destination = result;
                    // A scalar or collection result (a conversion, a list of DTOs) has no members to compare; it ran without throwing.
                    if (IsScalar(result) || result is IEnumerable) continue;
                    written = ChangedMembers(TryCreateEmpty(result.GetType()), result, result.GetType());
                }

                var expected = new HashSet<string>(writeSet, StringComparer.Ordinal);
                var unexpected = written.Where(w => !expected.Contains(w)).OrderBy(w => w, StringComparer.Ordinal).ToList();
                if (unexpected.Count > 0) problems.Add(name + ": writes members outside its write set: " + string.Join(", ", unexpected));
                // A member in the write set that kept its default is only wrong when it is copied from a filled source member
                // (constants and parameters can legitimately equal the default).
                var missing = writeSet.Where(m => !written.Contains(m) && lineage != null && lineage.TryGetValue(m, out var path) &&
                                                  TryResolve(source, path, out var value) && !IsDefault(value))
                    .OrderBy(m => m, StringComparer.Ordinal).ToList();
                if (missing.Count > 0) problems.Add(name + ": does not write " + string.Join(", ", missing) + " although the source has values");
                if (lineage != null)
                    problems.AddRange(LineageProblems(source, destination, lineage).Select(p => name + ": " + p));
            }

            if (checkedCount == 0)
                throw new MapperAssertException("'" + mapperType.Name + "' has no generated create or update method this check can run.");
            if (problems.Count > 0)
                throw new MapperAssertException("Mapping methods of " + mapperType.Name + " that do not match their generated write sets:\n" +
                                                string.Join("\n", problems.Select(p => "  " + p)));
        }

        // ------------------------------------------------------------------ round trips

        /// <summary>
        /// For a map and its reverse: <paramref name="back"/>(<paramref name="to"/>(x)) must equal x, member by member and
        /// deeply (collections item by item). With <paramref name="runs"/> above 1, or options with a seed, each run uses
        /// randomized sample data (nulls, empty and long strings, extremes) with seeds seed, seed+1, ..., and a failure names
        /// the seed so it can be replayed. <paramref name="ignore"/> takes member paths such as "Id" or "Address.Country".
        /// </summary>
        public static void RoundTrips<TA, TB>(Func<TA, TB> to, Func<TB, TA> back, IEnumerable<string>? ignore = null, SampleDataOptions? options = null, int runs = 1)
        {
            if (to == null) throw new ArgumentNullException(nameof(to));
            if (back == null) throw new ArgumentNullException(nameof(back));
            if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs));
            var ignored = new HashSet<string>(ignore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var randomized = runs > 1 || options?.Seed != null;
            var firstSeed = options?.Seed ?? 1;

            for (var run = 0; run < runs; run++)
            {
                var runOptions = randomized ? (options ?? new SampleDataOptions()).WithSeed(firstSeed + run) : options;
                var label = runOptions?.Describe() ?? "default values";
                var original = SampleData.Create<TA>(runOptions);
                TA again;
                try
                {
                    again = back(to(original));
                }
                catch (Exception e) when (!(e is MapperAssertException))
                {
                    throw new MapperAssertException("Round trip " + typeof(TA).Name + " → " + typeof(TB).Name + " → " + typeof(TA).Name +
                                                    " threw " + e.GetType().Name + " (" + label + "): " + e.Message, e);
                }

                var differences = new List<string>();
                Compare(original, again, string.Empty, ignored, differences, 0);
                if (differences.Count > 0)
                    throw new MapperAssertException("Round trip " + typeof(TA).Name + " → " + typeof(TB).Name + " → " + typeof(TA).Name +
                                                    " changed values (" + label + "):\n" + string.Join("\n", differences.Take(20).Select(d => "  " + d)) +
                                                    (differences.Count > 20 ? "\n  ... and " + (differences.Count - 20) + " more" : string.Empty));
            }
        }

        private static void Compare(object? expected, object? actual, string path, HashSet<string> ignored, List<string> differences, int depth)
        {
            if (path.Length > 0 && ignored.Contains(path)) return;
            var label = path.Length == 0 ? "(value)" : path;
            if (ReferenceEquals(expected, actual)) return;
            if (expected == null || actual == null)
            {
                differences.Add(label + ": " + Show(expected) + " became " + Show(actual));
                return;
            }
            if (IsScalar(expected) || IsScalar(actual))
            {
                if (!expected.Equals(actual)) differences.Add(label + ": " + Show(expected) + " became " + Show(actual));
                return;
            }
            if (depth > SampleData.MaxDepth + 2) return;
            if (expected is IEnumerable left && actual is IEnumerable right)
            {
                var a = left.Cast<object?>().ToList();
                var b = right.Cast<object?>().ToList();
                if (a.Count != b.Count)
                {
                    differences.Add(label + ": " + a.Count + " item(s) became " + b.Count);
                    return;
                }
                for (var i = 0; i < a.Count; i++) Compare(a[i], b[i], label + "[" + i + "]", ignored, differences, depth + 1);
                return;
            }
            if (expected.GetType() != actual.GetType())
            {
                differences.Add(label + ": a " + expected.GetType().Name + " became a " + actual.GetType().Name);
                return;
            }
            foreach (var member in Members(expected.GetType()))
                Compare(member.Get(expected), member.Get(actual), path.Length == 0 ? member.Name : path + "." + member.Name, ignored, differences, depth + 1);
        }

        // ------------------------------------------------------------------ unused mappers

        /// <summary>
        /// Fails for mapping methods of <paramref name="mapperType"/> that no code in <paramref name="productionAssemblies"/>
        /// uses (directly, or through another used mapping method of the class, as nested objects do): maps that only tests
        /// call, or nothing does, are candidates for deletion.
        /// </summary>
        public static void AllMethodsUsed(Type mapperType, params Assembly[] productionAssemblies)
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            if (productionAssemblies == null || productionAssemblies.Length == 0)
                throw new ArgumentException("Pass the assemblies whose code should use the maps (not the test assembly).", nameof(productionAssemblies));

            var methods = MappingMethods(mapperType);
            var byKey = methods.ToDictionary(Key, m => m, StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in productionAssemblies.Distinct())
                used.UnionWith(IlScanner.CalledMethods(assembly, mapperType).Where(byKey.ContainsKey));

            // Methods reached through a used method of the mapper itself (nested objects, [MapInherit] overloads calling each other).
            var all = mapperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .ToDictionary(Key, m => (MethodBase)m, StringComparer.Ordinal);
            var queue = new Queue<string>(used);
            var visited = new HashSet<string>(used, StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                if (!all.TryGetValue(queue.Dequeue(), out var method)) continue;
                foreach (var reference in IlScanner.ReferencedBy(method))
                {
                    if (!all.ContainsKey(reference) || !visited.Add(reference)) continue;
                    if (byKey.ContainsKey(reference)) used.Add(reference);
                    queue.Enqueue(reference);
                }
            }

            var unused = methods.Where(m => !used.Contains(Key(m))).Select(Describe).OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (unused.Count > 0)
                throw new MapperAssertException("Mapping methods of " + mapperType.Name + " that no code in " +
                                                string.Join(", ", productionAssemblies.Select(a => a.GetName().Name)) +
                                                " uses (only tests, or nothing; candidates for deletion):\n" + string.Join("\n", unused.Select(m => "  " + m)));
        }
    }
}
