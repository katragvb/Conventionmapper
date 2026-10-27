using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ConventionMapper.Testing
{
    /// <summary>
    /// How <see cref="SampleData"/> fills objects:
    /// <code>
    /// var options = new SampleDataOptions()
    ///     .For&lt;Address, string&gt;(a =&gt; a.ZipCode, n =&gt; (10000 + n).ToString())   // valid values for one member
    ///     .For&lt;Money&gt;(() =&gt; new Money(10m, "EUR"))                            // every value of a type
    ///     .Enum(Status.Active, Status.Closed)                                    // only these enum values
    ///     .Construct(() =&gt; Policy.CreateDraft())                                 // types without a usable constructor
    ///     .Randomized(seed: 42);                                                 // nulls, empty and long strings, extremes
    /// </code>
    /// </summary>
    public sealed class SampleDataOptions
    {
        internal static readonly SampleDataOptions Default = new SampleDataOptions();

        private readonly Dictionary<Type, Func<int, object?>> _types = new Dictionary<Type, Func<int, object?>>();
        private readonly List<(Type Owner, string Name, Func<int, object?> Value)> _members = new List<(Type, string, Func<int, object?>)>();
        private readonly Dictionary<string, Func<int, object?>> _names = new Dictionary<string, Func<int, object?>>(StringComparer.Ordinal);
        private readonly Dictionary<Type, List<object>> _enums = new Dictionary<Type, List<object>>();
        private readonly Dictionary<Type, Func<object?>> _factories = new Dictionary<Type, Func<object?>>();
        private int _maxDepth = SampleData.MaxDepth;
        private int _collectionSize = 1;

        /// <summary>Every value of type <typeparamref name="T"/> (and <c>T?</c>) comes from <paramref name="value"/>.</summary>
        public SampleDataOptions For<T>(Func<T> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _types[typeof(T)] = _ => value();
            return this;
        }

        /// <summary>Every value of type <typeparamref name="T"/> from <paramref name="value"/>, which gets a running number for unique values.</summary>
        public SampleDataOptions For<T>(Func<int, T> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _types[typeof(T)] = n => value(n);
            return this;
        }

        /// <summary>The member <paramref name="member"/> of <typeparamref name="TOwner"/> (and types derived from it) gets <paramref name="value"/>.</summary>
        public SampleDataOptions For<TOwner, TMember>(Expression<Func<TOwner, TMember>> member, Func<TMember> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _members.Add((typeof(TOwner), MemberName(member), _ => value()));
            return this;
        }

        /// <summary>The member <paramref name="member"/> gets <paramref name="value"/>, which gets a running number for unique values.</summary>
        public SampleDataOptions For<TOwner, TMember>(Expression<Func<TOwner, TMember>> member, Func<int, TMember> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _members.Add((typeof(TOwner), MemberName(member), n => value(n)));
            return this;
        }

        /// <summary>Every member (or constructor parameter) named <paramref name="name"/>, in any type, gets <paramref name="value"/>.</summary>
        public SampleDataOptions ForMember(string name, Func<object?> value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A member name is required.", nameof(name));
            if (value == null) throw new ArgumentNullException(nameof(value));
            _names[name] = _ => value();
            return this;
        }

        /// <summary>Enum members of <typeparamref name="TEnum"/> are only ever one of <paramref name="values"/>.</summary>
        public SampleDataOptions Enum<TEnum>(params TEnum[] values) where TEnum : struct, System.Enum
        {
            if (values == null || values.Length == 0) throw new ArgumentException("At least one value is required.", nameof(values));
            _enums[typeof(TEnum)] = values.Cast<object>().ToList();
            return this;
        }

        /// <summary>
        /// Creates <typeparamref name="T"/> with <paramref name="factory"/> (for types without a usable constructor, or an
        /// interface or abstract type); its remaining settable members are filled as usual.
        /// </summary>
        public SampleDataOptions Construct<T>(Func<T> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _factories[typeof(T)] = () => factory();
            return this;
        }

        /// <summary>
        /// Random edge cases, repeatable with <paramref name="seed"/>: nullable members are sometimes null, strings are sometimes
        /// empty, blank, very long or non-ASCII, numbers are sometimes 0 or their minimum or maximum, collections have 0 to 3 items.
        /// Values no longer carry member names, so use the default mode for WritesFrom.
        /// </summary>
        public SampleDataOptions Randomized(int seed)
        {
            Seed = seed;
            return this;
        }

        /// <summary>The seed of the random mode, or null for the default, deterministic values.</summary>
        public int? Seed { get; private set; }

        /// <summary>Maximum depth of nested objects (default <see cref="SampleData.MaxDepth"/>).</summary>
        public int MaxDepth
        {
            get => _maxDepth;
            set => _maxDepth = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Items per collection in the default mode (default 1); the random mode uses 0 to this or 3, whichever is larger.</summary>
        public int CollectionSize
        {
            get => _collectionSize;
            set => _collectionSize = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Also fill properties with private or init-only setters (entities that guard their state). Default false.</summary>
        public bool FillNonPublicSetters { get; set; }

        /// <summary>An independent copy: changes to it don't affect these options (to add values for one run).</summary>
        public SampleDataOptions Clone()
        {
            var copy = new SampleDataOptions { FillNonPublicSetters = FillNonPublicSetters, Seed = Seed };
            foreach (var pair in _types) copy._types[pair.Key] = pair.Value;
            copy._members.AddRange(_members);
            foreach (var pair in _names) copy._names[pair.Key] = pair.Value;
            foreach (var pair in _enums) copy._enums[pair.Key] = new List<object>(pair.Value);
            foreach (var pair in _factories) copy._factories[pair.Key] = pair.Value;
            copy._maxDepth = _maxDepth;
            copy._collectionSize = _collectionSize;
            return copy;
        }

        /// <summary>A copy with another seed (used for repeated randomized runs).</summary>
        internal SampleDataOptions WithSeed(int seed)
        {
            var copy = (SampleDataOptions)MemberwiseClone();
            copy.Seed = seed;
            return copy;
        }

        internal bool TryType(Type type, int n, out object? value)
        {
            if (_types.TryGetValue(type, out var make))
            {
                value = make(n);
                return true;
            }
            value = null;
            return false;
        }

        internal bool TryMember(Type owner, string name, int n, out object? value)
        {
            // The most derived owner wins when several overrides match.
            for (var i = _members.Count - 1; i >= 0; i--)
            {
                var (o, m, make) = _members[i];
                if (m == name && o.IsAssignableFrom(owner))
                {
                    value = make(n);
                    return true;
                }
            }
            value = null;
            return false;
        }

        internal bool TryName(string name, int n, out object? value)
        {
            if (_names.TryGetValue(name, out var make))
            {
                value = make(n);
                return true;
            }
            value = null;
            return false;
        }

        internal List<object>? EnumValues(Type type) => _enums.TryGetValue(type, out var values) ? values : null;

        internal bool HasFactory(Type type) => _factories.ContainsKey(type);

        internal bool TryFactory(Type type, out object? instance)
        {
            if (_factories.TryGetValue(type, out var factory))
            {
                instance = factory();
                return true;
            }
            instance = null;
            return false;
        }

        private static string MemberName(LambdaExpression member)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            var body = member.Body is UnaryExpression convert && convert.NodeType == ExpressionType.Convert ? convert.Operand : member.Body;
            if (body is MemberExpression access && access.Expression == member.Parameters[0] && (access.Member is PropertyInfo || access.Member is FieldInfo))
                return access.Member.Name;
            throw new ArgumentException("Expected a member of the parameter, like x => x.ZipCode; got " + member.Body.ToString() + ".", nameof(member));
        }

        /// <summary>For messages: "seed 42" or "default values".</summary>
        internal string Describe() => Seed.HasValue ? "seed " + Seed.Value.ToString(CultureInfo.InvariantCulture) : "default values";
    }
}
