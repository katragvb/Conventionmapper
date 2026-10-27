using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ConventionMapper.Testing
{
    /// <summary>Assertions for mapping methods. Failures throw <see cref="MapperAssertException"/>, which every test framework reports.</summary>
    public static partial class MapperAssert
    {
        // ------------------------------------------------------------------ write sets

        /// <summary>
        /// Maps a source with every member filled (<see cref="SampleData"/>) and checks that exactly the
        /// <paramref name="expected"/> destination members got a value. Pass a generated write set to compare with what
        /// the generator wrote: <c>MapperAssert.WritesExactly((Order o) =&gt; o.ToDto(), OrderMapper.WriteSets["ToDto"])</c>.
        /// </summary>
        /// <param name="map">The mapping to run, for example <c>(LocationData s) =&gt; s.ToImportData("GL1", 10m)</c>.</param>
        /// <param name="expected">Destination members the map must write (top level; a nested object counts as one member).</param>
        /// <param name="ignore">Members to leave out of the comparison, such as ones the destination's constructor sets to a fresh value.</param>
        public static void WritesExactly<TSource, TDestination>(Func<TSource, TDestination> map, IEnumerable<string> expected, IEnumerable<string>? ignore = null)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var source = SampleData.Create<TSource>();
            var result = map(source);
            if (result == null) throw new MapperAssertException("The mapping returned null for a filled " + typeof(TSource).Name + ".");

            var baseline = TryCreateEmpty(result.GetType());
            var written = ChangedMembers(baseline, result, result.GetType());
            Compare(typeof(TSource), result.GetType(), expected, ignore, written);
        }

        /// <summary>Same as <see cref="WritesExactly{TSource, TDestination}(Func{TSource, TDestination}, IEnumerable{string}, IEnumerable{string})"/>, with the members listed inline.</summary>
        public static void WritesExactly<TSource, TDestination>(Func<TSource, TDestination> map, params string[] expected) =>
            WritesExactly(map, (IEnumerable<string>)expected);

        /// <summary>
        /// For update methods: runs <paramref name="update"/> with a filled source on a fresh target (from
        /// <paramref name="createTarget"/>, or its parameterless constructor) and checks that exactly the
        /// <paramref name="expected"/> members changed.
        /// </summary>
        public static void UpdatesExactly<TSource, TTarget>(Action<TSource, TTarget> update, IEnumerable<string> expected,
            Func<TTarget>? createTarget = null, IEnumerable<string>? ignore = null)
            where TTarget : class
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            TTarget target;
            if (createTarget != null) target = createTarget();
            else
            {
                if (!(TryCreateEmpty(typeof(TTarget)) is TTarget created))
                    throw new MapperAssertException(typeof(TTarget).Name + " has no parameterless constructor; pass createTarget.");
                target = created;
            }

            var before = Snapshot(target, typeof(TTarget));
            update(SampleData.Create<TSource>(), target);
            var after = Snapshot(target, typeof(TTarget));
            var changed = before.Keys.Where(k => !Same(before[k], after[k])).ToList();
            Compare(typeof(TSource), typeof(TTarget), expected, ignore, changed);
        }

        private static void Compare(Type source, Type destination, IEnumerable<string> expected, IEnumerable<string>? ignore, IEnumerable<string> written)
        {
            var ignored = new HashSet<string>(ignore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var expectedSet = new SortedSet<string>(expected.Where(e => !ignored.Contains(e)), StringComparer.Ordinal);
            var actualSet = new SortedSet<string>(written.Where(w => !ignored.Contains(w)), StringComparer.Ordinal);
            if (expectedSet.SetEquals(actualSet)) return;

            var unexpected = actualSet.Except(expectedSet).ToList();
            var missing = expectedSet.Except(actualSet).ToList();
            var message = "Mapping " + source.Name + " → " + destination.Name + " writes different members than expected.";
            if (unexpected.Count > 0) message += "\nWritten but not expected: " + string.Join(", ", unexpected);
            if (missing.Count > 0) message += "\nExpected but not written: " + string.Join(", ", missing);
            message += "\nWritten: " + (actualSet.Count == 0 ? "(nothing)" : string.Join(", ", actualSet));
            throw new MapperAssertException(message);
        }

        private static object? TryCreateEmpty(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return ctor?.Invoke(null);
        }

        private static List<string> ChangedMembers(object? baseline, object result, Type type)
        {
            var changed = new List<string>();
            foreach (var member in Members(type))
            {
                var before = baseline == null ? DefaultOf(member.Type) : member.Get(baseline);
                if (!Same(before, member.Get(result))) changed.Add(member.Name);
            }
            return changed;
        }

        private static Dictionary<string, object?> Snapshot(object target, Type type)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var member in Members(type))
            {
                var value = member.Get(target);
                // Collections are copied, so changes made in place are seen too.
                values[member.Name] = value is IEnumerable sequence && !(value is string) ? new SequenceCopy(value, sequence.Cast<object?>().ToList()) : value;
            }
            return values;
        }

        private sealed class SequenceCopy
        {
            public SequenceCopy(object instance, List<object?> items)
            {
                Instance = instance;
                Items = items;
            }

            public object Instance { get; }
            public List<object?> Items { get; }
        }

        private static bool Same(object? a, object? b)
        {
            if (a is SequenceCopy copyA && b is SequenceCopy copyB)
                return ReferenceEquals(copyA.Instance, copyB.Instance) && SequenceSame(copyA.Items, copyB.Items);
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is IEnumerable left && b is IEnumerable right && !(a is string))
                return SequenceSame(left.Cast<object?>().ToList(), right.Cast<object?>().ToList());
            return a.Equals(b);
        }

        private static bool SequenceSame(List<object?> a, List<object?> b) => a.Count == b.Count && a.Zip(b, Same).All(x => x);

        private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

        private readonly struct Member
        {
            public Member(string name, Type type, Func<object, object?> get)
            {
                Name = name;
                Type = type;
                Get = get;
            }

            public string Name { get; }
            public Type Type { get; }
            public Func<object, object?> Get { get; }
        }

        private static IEnumerable<Member> Members(Type type)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() == null) continue;
                if (property.Name == "EqualityContract") continue; // records
                var p = property;
                yield return new Member(p.Name, p.PropertyType, o => p.GetValue(o));
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var f = field;
                yield return new Member(f.Name, f.FieldType, o => f.GetValue(o));
            }
        }

        /// <summary>
        /// The generated write set of a mapping method: <c>WriteSets[key]</c> of a [GeneratedMapper] class, where the key
        /// is the method name (no overloads) or "Name(ParameterTypes)" as in the data-flow report.
        /// </summary>
        public static IReadOnlyList<string> WriteSetOf(Type mapperType, string method)
        {
            var sets = WriteSets(mapperType);
            if (sets.TryGetValue(method, out var set)) return set;
            throw new MapperAssertException("'" + mapperType.Name + "' has no write set '" + method + "'. Known: " + string.Join(", ", sets.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        /// <summary>
        /// The generated write set of a method by name and parameter types, through the mapper's generated
        /// <c>WriteSetOf</c>: <c>MapperAssert.WriteSetOf(typeof(Mappings), nameof(Mappings.ToDto), typeof(Order))</c>.
        /// A renamed method is a compile error (nameof), a changed signature a failure that lists the overloads.
        /// </summary>
        public static IReadOnlyList<string> WriteSetOf(Type mapperType, string method, params Type[] parameterTypes)
        {
            CheckGeneratorVersion(mapperType);
            var accessor = mapperType.GetMethod("WriteSetOf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Type[]) }, null);
            if (accessor == null)
                throw new MapperAssertException("'" + mapperType.FullName + "' has no generated WriteSetOf. Is it a [GeneratedMapper] class built with ConventionMapper.SourceGenerator 1.9 or later?");
            try
            {
                return (IReadOnlyList<string>)accessor.Invoke(null, new object[] { method, parameterTypes })!;
            }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException inner)
            {
                throw new MapperAssertException(inner.Message);
            }
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> WriteSets(Type mapperType)
        {
            CheckGeneratorVersion(mapperType);
            var field = mapperType.GetField("WriteSets", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is IReadOnlyDictionary<string, IReadOnlyList<string>> sets) return sets;
            throw new MapperAssertException("'" + mapperType.FullName + "' has no generated WriteSets. Is it a [GeneratedMapper] class built with ConventionMapper.SourceGenerator 1.8 or later?");
        }

        // ------------------------------------------------------------------ coverage

        /// <summary>
        /// Fails when a generated mapping method of <paramref name="mapperType"/> is never called (directly, through a
        /// method group, or inside an expression tree) by code in <paramref name="testAssembly"/>, so a new map cannot be
        /// added without a test. Calls from inside the mapper class itself (nested objects reuse its methods) do not count.
        /// </summary>
        public static void AllMethodsCovered(Type mapperType, Assembly testAssembly)
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            if (testAssembly == null) throw new ArgumentNullException(nameof(testAssembly));
            var methods = MappingMethods(mapperType);

            // The mapper's own generated code calls its methods for nested objects; that is not a test.
            var called = IlScanner.CalledMethods(testAssembly, mapperType);
            var missing = methods.Where(m => !called.Contains(Key(m))).Select(Describe).OrderBy(d => d, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                throw new MapperAssertException("Mapping methods of " + mapperType.Name + " that no test in " + testAssembly.GetName().Name + " calls:\n" + string.Join("\n", missing.Select(m => "  " + m)));
        }

        /// <summary>
        /// Stricter than <see cref="AllMethodsCovered"/>: every create and update method of <paramref name="mapperType"/> must
        /// run inside a <see cref="WritesExactly{TSource, TDestination}(Func{TSource, TDestination}, IEnumerable{string}, IEnumerable{string})"/>
        /// or <see cref="UpdatesExactly"/> check in <paramref name="testAssembly"/>, so a test that only calls a map without
        /// asserting anything does not count. Projections and collection merges (which those checks can't run) must be called at least.
        /// </summary>
        public static void AllMethodsVerified(Type mapperType, Assembly testAssembly)
        {
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            if (testAssembly == null) throw new ArgumentNullException(nameof(testAssembly));
            var methods = MappingMethods(mapperType);
            var called = IlScanner.CalledMethods(testAssembly, mapperType);
            var verified = IlScanner.VerifiedMethods(testAssembly, mapperType);

            var missing = new List<string>();
            foreach (var method in methods)
            {
                if (IsProjection(method) || IsMerge(method))
                {
                    if (!called.Contains(Key(method))) missing.Add("  " + Describe(method) + (IsMerge(method) ? " (merge, never called)" : " (projection, never called)"));
                }
                else if (!verified.Contains(Key(method)))
                {
                    missing.Add("  " + Describe(method) + (called.Contains(Key(method)) ? " (called, but not in WritesExactly/UpdatesExactly)" : " (never called)"));
                }
            }
            if (missing.Count > 0)
                throw new MapperAssertException("Mapping methods of " + mapperType.Name + " without a WritesExactly/UpdatesExactly check in " +
                                                testAssembly.GetName().Name + ":\n" + string.Join("\n", missing.OrderBy(m => m, StringComparer.Ordinal)));
        }

        private static List<MethodInfo> MappingMethods(Type mapperType)
        {
            var names = new HashSet<string>(WriteSets(mapperType).Keys.Select(k => k.IndexOf('(') >= 0 ? k.Substring(0, k.IndexOf('(')) : k), StringComparer.Ordinal);
            return mapperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => names.Contains(m.Name) && !m.IsSpecialName)
                .ToList();
        }

        private static bool IsProjection(MethodInfo method)
        {
            var returned = method.ReturnType;
            if (!returned.IsGenericType) return false;
            var definition = returned.GetGenericTypeDefinition();
            return definition == typeof(IQueryable<>) || definition == typeof(System.Linq.Expressions.Expression<>);
        }

        /// <summary>void Merge(IEnumerable&lt;S&gt;, ICollection&lt;T&gt;): a collection merge, which the write checks can't run.</summary>
        private static bool IsMerge(MethodInfo method)
        {
            var parameters = method.GetParameters();
            return method.ReturnType == typeof(void) && parameters.Length >= 2 &&
                   parameters[0].ParameterType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(parameters[0].ParameterType) &&
                   parameters[1].ParameterType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(parameters[1].ParameterType);
        }

        internal static string Key(MethodBase method) => method.Module.ModuleVersionId.ToString("N") + ":" + method.MetadataToken.ToString("X8");

        private static string Describe(MethodInfo method) =>
            method.DeclaringType!.Name + "." + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
    }
}
