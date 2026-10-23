using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>One destination value changed by a mapping, e.g. <c>Customer.Email: "a@x.com" -&gt; "b@x.com"</c>.</summary>
    public sealed class MemberChange
    {
        internal MemberChange(string path, object? oldValue, object? newValue, bool isSensitive)
        {
            Path = path;
            IsSensitive = isSensitive;
            OldValue = isSensitive ? null : oldValue;
            NewValue = isSensitive ? null : newValue;
        }

        /// <summary>Member path in the destination, e.g. <c>Email</c>, <c>Customer.Address.City</c>, <c>Lines[2].Quantity</c>, <c>Lines.Count</c>.</summary>
        public string Path { get; }

        /// <summary>The value before mapping (null for [Sensitive] members, whose values are never exposed).</summary>
        public object? OldValue { get; }

        /// <summary>The value after mapping (null for [Sensitive] members, whose values are never exposed).</summary>
        public object? NewValue { get; }

        /// <summary>True when the member (or an object containing it) is marked [Sensitive]; the values are redacted.</summary>
        public bool IsSensitive { get; }

        public override string ToString() =>
            IsSensitive ? Path + ": (sensitive value changed)" : Path + ": " + Format(OldValue) + " -> " + Format(NewValue);

        internal static string Format(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return "\"" + s + "\"";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString() ?? string.Empty;
            }
        }
    }

    /// <summary>The changes a mapping made to an existing object, in member order.</summary>
    public sealed class MappingChanges : IReadOnlyList<MemberChange>
    {
        private readonly List<MemberChange> _changes;

        internal MappingChanges(List<MemberChange> changes)
        {
            _changes = changes;
        }

        /// <summary>True when at least one value changed.</summary>
        public bool HasChanges => _changes.Count > 0;

        public int Count => _changes.Count;

        public MemberChange this[int index] => _changes[index];

        /// <summary>The change for a member path such as <c>"Customer.Email"</c>, or null when it did not change.</summary>
        public MemberChange? Find(string path) => _changes.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.Ordinal));

        /// <summary>True when the member at <paramref name="path"/>, or anything below it, changed.</summary>
        public bool Changed(string path) =>
            _changes.Any(c => c.Path == path || c.Path.StartsWith(path + ".", StringComparison.Ordinal) || c.Path.StartsWith(path + "[", StringComparison.Ordinal));

        public IEnumerator<MemberChange> GetEnumerator() => _changes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>e.g. <c>Email: "a@x.com" -&gt; "b@x.com"; Status: Pending -&gt; Shipped</c>.</summary>
        public override string ToString() => _changes.Count == 0 ? "(no changes)" : string.Join("; ", _changes);
    }

    public static class ChangeTrackingExtensions
    {
        /// <summary>
        /// Maps <paramref name="source"/> into the existing <paramref name="destination"/> and returns exactly which values changed,
        /// with their old and new values, for audit logs, change events, PATCH responses or skipping no-op saves.
        /// Members marked [Sensitive] are reported as changed with their values redacted.
        /// </summary>
        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static MappingChanges MapWithChanges<TSource, TDestination>(this IMapper mapper, TSource source, TDestination destination)
            where TDestination : class =>
            MapWithChanges(mapper, source, destination, null);

        /// <summary>
        /// Like <see cref="MapWithChanges{TSource, TDestination}(IMapper, TSource, TDestination)"/>, with per-call options
        /// (for example <c>opt =&gt; opt.IgnoreNullSourceValues()</c> for PATCH semantics).
        /// </summary>
        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static MappingChanges MapWithChanges<TSource, TDestination>(this IMapper mapper, TSource source, TDestination destination,
            Action<IMappingOperationOptions>? opts)
            where TDestination : class
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var before = ObjectSnapshot.Take(destination);
            var result = opts == null ? mapper.Map(source, destination) : mapper.Map(source, destination, opts);
            var after = ObjectSnapshot.Take(result!);
            return ObjectSnapshot.Diff(before, after);
        }
    }
}

namespace ConventionMapper.Internal
{
    /// <summary>A flat copy of an object graph's readable values, keyed by member path.</summary>
    internal sealed class ObjectSnapshot
    {
        private const int MaxDepth = 8;

        private static readonly ConcurrentDictionary<Type, Getter[]> Getters = new ConcurrentDictionary<Type, Getter[]>();

        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly struct Entry
        {
            public Entry(object? value, bool sensitive)
            {
                Value = value;
                Sensitive = sensitive;
            }

            public object? Value { get; }
            public bool Sensitive { get; }
        }

        private sealed class Getter
        {
            public Getter(string name, Func<object, object?> get, bool sensitive)
            {
                Name = name;
                Get = get;
                Sensitive = sensitive;
            }

            public string Name { get; }
            public Func<object, object?> Get { get; }
            public bool Sensitive { get; }
        }

        /// <summary>Collections of scalars are compared as a whole: [1, 2] -&gt; [1, 2, 3].</summary>
        private sealed class SequenceValue
        {
            private readonly object?[] _items;

            public SequenceValue(IEnumerable items) { _items = items.Cast<object?>().ToArray(); }

            public override bool Equals(object? obj) => obj is SequenceValue other && _items.SequenceEqual(other._items);

            public override int GetHashCode() => _items.Length;

            public override string ToString() => "[" + string.Join(", ", _items.Select(MemberChange.Format)) + "]";
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        public static ObjectSnapshot Take(object root)
        {
            var snapshot = new ObjectSnapshot();
            snapshot.VisitMembers(root, string.Empty, false, 0, new HashSet<object>(ReferenceComparer.Instance));
            return snapshot;
        }

        public static MappingChanges Diff(ObjectSnapshot before, ObjectSnapshot after)
        {
            var changes = new List<MemberChange>();
            var paths = new List<string>(before._order);
            foreach (var path in after._order)
                if (!before._entries.ContainsKey(path)) paths.Add(path);

            foreach (var path in paths)
            {
                var hadOld = before._entries.TryGetValue(path, out var oldEntry);
                var hasNew = after._entries.TryGetValue(path, out var newEntry);
                var oldValue = hadOld ? oldEntry.Value : null;
                var newValue = hasNew ? newEntry.Value : null;
                if (Equals(oldValue, newValue)) continue;
                changes.Add(new MemberChange(path, oldValue, newValue, oldEntry.Sensitive || newEntry.Sensitive));
            }
            return new MappingChanges(changes);
        }

        private void Add(string path, object? value, bool sensitive)
        {
            if (_entries.ContainsKey(path)) return;
            _order.Add(path);
            _entries[path] = new Entry(value, sensitive);
        }

        private void Visit(object? value, string path, bool sensitive, int depth, HashSet<object> visiting)
        {
            if (value == null || IsScalar(value.GetType()) || depth >= MaxDepth)
            {
                Add(path, value, sensitive);
                return;
            }

            if (value is IEnumerable items)
            {
                var element = ElementType(value.GetType());
                if (element == null || IsScalar(element))
                {
                    Add(path, new SequenceValue(items), sensitive);
                    return;
                }
                var index = 0;
                foreach (var item in items)
                {
                    Visit(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", sensitive, depth + 1, visiting);
                    index++;
                }
                Add(path + ".Count", index, false);
                return;
            }

            VisitMembers(value, path, sensitive, depth, visiting);
        }

        private void VisitMembers(object value, string path, bool sensitive, int depth, HashSet<object> visiting)
        {
            if (!visiting.Add(value)) return; // cycles are reported once
            try
            {
                foreach (var getter in GettersFor(value.GetType()))
                {
                    object? member;
                    try
                    {
                        member = getter.Get(value);
                    }
                    catch (Exception)
                    {
                        continue; // a throwing getter is not a mapped value
                    }
                    Visit(member, path.Length == 0 ? getter.Name : path + "." + getter.Name, sensitive || getter.Sensitive, depth + 1, visiting);
                }
            }
            finally
            {
                visiting.Remove(value);
            }
        }

        private static bool IsScalar(Type type) =>
            type.IsValueType || type == typeof(string) || type == typeof(Uri) || type == typeof(Version) || typeof(Type).IsAssignableFrom(type);

        private static Type? ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            foreach (var i in type.GetInterfaces().Concat(new[] { type }))
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return i.GetGenericArguments()[0];
            return null;
        }

        private static Getter[] GettersFor(Type type) => Getters.GetOrAdd(type, t =>
        {
            var result = new List<Getter>();
            foreach (var property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() == null) continue;
                result.Add(new Getter(property.Name, Compile(t, property), SensitiveGuard.IsSensitive(property)));
            }
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                result.Add(new Getter(field.Name, Compile(t, field), SensitiveGuard.IsSensitive(field)));
            return result.ToArray();
        });

        private static Func<object, object?> Compile(Type type, MemberInfo member)
        {
            var instance = Expression.Parameter(typeof(object), "instance");
            var access = Expression.MakeMemberAccess(Expression.Convert(instance, type), member);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), instance).Compile();
        }
    }
}
