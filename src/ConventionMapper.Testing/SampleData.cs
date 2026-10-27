using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace ConventionMapper.Testing
{
    /// <summary>
    /// Builds objects with every member filled: distinct non-default scalars (strings carry the member name, so a value
    /// that lands in the wrong member is easy to spot), nested objects, and collections with one element.
    /// Pass <see cref="SampleDataOptions"/> for valid domain values, enum subsets, factories, or randomized edge cases.
    /// </summary>
    public static class SampleData
    {
        /// <summary>Maximum depth of nested objects; deeper members are left at their default.</summary>
        public const int MaxDepth = 4;

        /// <summary>A new <typeparamref name="T"/> with every settable member filled.</summary>
        public static T Create<T>() => (T)Create(typeof(T))!;

        /// <summary>A new instance of <paramref name="type"/> with every settable member filled, or null when it cannot be built.</summary>
        public static object? Create(Type type) => Create(type, null);

        /// <summary>A new <typeparamref name="T"/> built with <paramref name="options"/>.</summary>
        public static T Create<T>(SampleDataOptions? options) => (T)Create(typeof(T), options)!;

        /// <summary>A new instance of <paramref name="type"/> built with <paramref name="options"/>, or null when it cannot be built.</summary>
        public static object? Create(Type type, SampleDataOptions? options)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return new Filler(options ?? SampleDataOptions.Default).Value(type, type.Name, 0, null);
        }

        /// <summary>
        /// <paramref name="count"/> instances that share one sequence, so every value is distinct across them too
        /// (not only within one object).
        /// </summary>
        internal static List<object?> CreateMany(Type type, int count, SampleDataOptions? options)
        {
            var filler = new Filler(options ?? SampleDataOptions.Default);
            var result = new List<object?>(count);
            for (var i = 0; i < count; i++) result.Add(filler.Value(type, type.Name, 0, null));
            return result;
        }

        /// <summary>
        /// A fluent builder for a test object: starts from a filled <typeparamref name="T"/> (or a blank one with
        /// <see cref="Builder{T}.Blank"/>) and sets the members a test cares about:
        /// <c>SampleData.Builder&lt;Referral&gt;().With(r =&gt; r.Status, "A").With(r =&gt; r.Address.City, "Leeds").Build()</c>.
        /// </summary>
        public static Builder<T> Builder<T>(SampleDataOptions? options = null) => new Builder<T>(options);

        /// <summary>A value for a method parameter (used by MapperAssert for the extra parameters of a mapping method).</summary>
        internal static object? CreateParameter(ParameterInfo parameter, SampleDataOptions? options) =>
            new Filler(options ?? SampleDataOptions.Default).Value(parameter.ParameterType, parameter.Name ?? parameter.ParameterType.Name, 0,
                new Slot(parameter.Member.DeclaringType, parameter.Name, parameter));

        /// <summary>Where a value goes: the type that declares the member, its name, and the member for nullability.</summary>
        private readonly struct Slot
        {
            public Slot(Type? owner, string? name, ICustomAttributeProvider? member)
            {
                Owner = owner;
                Name = name;
                Member = member;
            }

            public Type? Owner { get; }
            public string? Name { get; }
            public ICustomAttributeProvider? Member { get; }
        }

        private sealed class Filler
        {
            private readonly SampleDataOptions _options;
            private readonly Random? _random;
            private readonly HashSet<Type> _building = new HashSet<Type>();
            private int _counter;

            public Filler(SampleDataOptions options)
            {
                _options = options;
                _random = options.Seed.HasValue ? new Random(options.Seed.Value) : null;
            }

            public object? Value(Type type, string name, int depth, Slot? slot)
            {
                var underlying = Nullable.GetUnderlyingType(type);
                var n = ++_counter;

                // Overrides: the member, then any member of that name, then the type.
                if (slot?.Owner != null && slot.Value.Name != null && _options.TryMember(slot.Value.Owner, slot.Value.Name, n, out var memberValue)) return memberValue;
                if (slot?.Name != null && _options.TryName(slot.Value.Name, n, out var namedValue)) return namedValue;
                if (_options.TryType(type, n, out var typedValue)) return typedValue;
                if (underlying != null && _options.TryType(underlying, n, out typedValue)) return typedValue;

                // Random mode: a nullable member is null now and then.
                if (_random != null && (underlying != null || slot?.Member != null && !type.IsValueType && IsNullableReference(slot.Value)) &&
                    _random.Next(5) == 0)
                    return null;
                if (underlying != null) type = underlying;

                if (type == typeof(string)) return Text(name, n);
                if (type == typeof(object)) return name + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return _random == null || _random.Next(2) == 0;
                if (type == typeof(char)) return (char)('a' + (_random?.Next(26) ?? n % 26));
                if (type.IsEnum) return EnumValue(type);
                if (type == typeof(decimal)) return Pick(n + 0.5m, 0m, -1.25m, 79228162514264337593543950m);
                if (type == typeof(double)) return Pick(n + 0.5d, 0d, -1.25d, 1e300);
                if (type == typeof(float)) return Pick(n + 0.5f, 0f, -1.25f, 3e38f);
                if (type.IsPrimitive) return Number(type, n);
                if (type == typeof(Guid)) return _random == null ? new Guid(n, 0, 0, new byte[8]) : Pick(Guid.NewGuid(), Guid.Empty);
                if (type == typeof(DateTime))
                    return _random == null
                        ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n)
                        : Pick(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_random.Next(5_000_000)), DateTime.MinValue, new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
                if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(_random?.Next(3000) ?? n);
                if (type == typeof(TimeSpan)) return TimeSpan.FromMinutes(_random?.Next(-1000, 100_000) ?? n);
                if (type == typeof(Uri)) return new Uri("https://example.com/" + name + "/" + n.ToString(CultureInfo.InvariantCulture));
                if (type.FullName == "System.DateOnly") return Activator.CreateInstance(type, 2020, 1, (_random?.Next(28) ?? n % 28) + 1);
                if (type.FullName == "System.TimeOnly") return Activator.CreateInstance(type, _random?.Next(24) ?? n % 24, 0);

                if (depth >= _options.MaxDepth) return Default(type);
                if (type.IsArray) return Array(type, name, depth);
                var collection = Collection(type, name, depth);
                if (collection != null) return collection;
                if (type.IsPointer || type.IsByRef || type.ContainsGenericParameters) return Default(type);
                if ((type.IsAbstract || type.IsInterface) && !_options.HasFactory(type)) return Default(type);
                return Object(type, name, depth);
            }

            private string Text(string name, int n)
            {
                var plain = name + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (_random == null) return plain;
                switch (_random.Next(10))
                {
                    case 0: return string.Empty;
                    case 1: return " ";
                    case 2: return plain + new string('x', 300);
                    case 3: return "Ünïcødé ✓ " + plain;
                    default: return plain;
                }
            }

            private T Pick<T>(T usual, params T[] edges) =>
                _random == null || _random.Next(3) != 0 ? usual : edges[_random.Next(edges.Length)];

            private object Number(Type type, int n)
            {
                if (_random == null) return Convert.ChangeType(n % 100 + 1, type, CultureInfo.InvariantCulture);
                var usual = Convert.ChangeType(_random.Next(1, 100), type, CultureInfo.InvariantCulture);
                if (_random.Next(3) != 0) return usual;
                var max = type.GetField("MaxValue")?.GetValue(null);
                var min = type.GetField("MinValue")?.GetValue(null);
                var zero = Convert.ChangeType(0, type, CultureInfo.InvariantCulture);
                var options = new[] { zero, max, min }.Where(o => o != null).ToArray();
                return options[_random.Next(options.Length)]!;
            }

            private object EnumValue(Type type)
            {
                var values = _options.EnumValues(type) ?? Enum.GetValues(type).Cast<object>().ToList();
                if (values.Count == 0) return Activator.CreateInstance(type)!;
                if (_random != null) return values[_random.Next(values.Count)];
                // A non-zero value, so it differs from the default.
                return values.FirstOrDefault(v => System.Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0) ?? values[0];
            }

            private int Count() => _random == null ? _options.CollectionSize : _random.Next(0, Math.Max(_options.CollectionSize, 3) + 1);

            private object? Array(Type type, string name, int depth)
            {
                var element = type.GetElementType()!;
                var count = Count();
                var array = System.Array.CreateInstance(element, count);
                for (var i = 0; i < count; i++) array.SetValue(Value(element, name, depth + 1, null), i);
                return array;
            }

            private object? Collection(Type type, string name, int depth)
            {
                var dictionary = FindGeneric(type, typeof(IDictionary<,>)) ?? FindGeneric(type, typeof(IReadOnlyDictionary<,>));
                if (dictionary != null)
                {
                    var args = dictionary.GetGenericArguments();
                    var target = Instantiate(type, typeof(Dictionary<,>).MakeGenericType(args));
                    if (target == null) return null;
                    var add = target.GetType().GetMethod("Add", new[] { args[0], args[1] });
                    var contains = target.GetType().GetMethod("ContainsKey", new[] { args[0] });
                    var count = Count();
                    for (var i = 0; i < count; i++)
                    {
                        var key = Value(args[0], name + "Key", depth + 1, null);
                        if (key == null || contains != null && (bool)contains.Invoke(target, new[] { key })!) continue;
                        add?.Invoke(target, new[] { key, Value(args[1], name + "Value", depth + 1, null) });
                    }
                    return target;
                }

                var enumerable = type == typeof(string) ? null : FindGeneric(type, typeof(IEnumerable<>));
                if (enumerable == null) return null;
                var elementType = enumerable.GetGenericArguments()[0];
                var isSet = FindGeneric(type, typeof(ISet<>)) != null;
                var created = Instantiate(type, (isSet ? typeof(HashSet<>) : typeof(List<>)).MakeGenericType(elementType));
                if (created == null) return null;
                var addItem = created.GetType().GetMethod("Add", new[] { elementType });
                var items = Count();
                for (var i = 0; i < items; i++) addItem?.Invoke(created, new[] { Value(elementType, name + "Item", depth + 1, null) });
                return created;
            }

            /// <summary>The type itself when it can be created, otherwise the standard implementation when it is assignable.</summary>
            private static object? Instantiate(Type requested, Type fallback)
            {
                if (!requested.IsAbstract && !requested.IsInterface && requested.GetConstructor(Type.EmptyTypes) != null)
                    return Activator.CreateInstance(requested);
                return requested.IsAssignableFrom(fallback) ? Activator.CreateInstance(fallback) : null;
            }

            private static Type? FindGeneric(Type type, Type definition)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == definition) return type;
                return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
            }

            private object? Object(Type type, string name, int depth)
            {
                if (!_building.Add(type)) return Default(type); // a type that contains itself: stop here
                try
                {
                    object? instance;
                    var filledByConstructor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (_options.TryFactory(type, out var made))
                    {
                        instance = made;
                        if (instance == null) return null;
                        type = instance.GetType();
                    }
                    else
                    {
                        var ctor = type.GetConstructors().Where(c => !IsCopyConstructor(c, type)).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                        if (ctor != null && ctor.GetParameters().Length > 0)
                        {
                            var parameters = ctor.GetParameters();
                            var args = parameters.Select(p => Value(p.ParameterType, p.Name ?? name, depth + 1,
                                new Slot(type, p.Name == null ? null : MemberName(type, p.Name), p))).ToArray();
                            instance = ctor.Invoke(args);
                            foreach (var p in parameters) if (p.Name != null) filledByConstructor.Add(p.Name);
                        }
                        else if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null)
                        {
                            instance = Activator.CreateInstance(type);
                        }
                        else
                        {
                            // EF entities and serializer models often have a private or protected parameterless constructor.
                            var hidden = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (hidden == null) return null;
                            instance = hidden.Invoke(null);
                        }
                    }

                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (property.GetIndexParameters().Length > 0 || filledByConstructor.Contains(property.Name)) continue;
                        // Public setters, plus init-only and private setters of members a factory or constructor did not set.
                        var setter = property.GetSetMethod(nonPublic: false) ?? (_options.FillNonPublicSetters ? property.GetSetMethod(nonPublic: true) : null);
                        if (setter == null) continue;
                        property.SetValue(instance, Value(property.PropertyType, property.Name, depth + 1, new Slot(type, property.Name, property)));
                    }
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (field.IsInitOnly || filledByConstructor.Contains(field.Name)) continue;
                        field.SetValue(instance, Value(field.FieldType, field.Name, depth + 1, new Slot(type, field.Name, field)));
                    }
                    return instance;
                }
                finally
                {
                    _building.Remove(type);
                }
            }

            /// <summary>The property a constructor parameter fills (records: "name" → "Name"), so member overrides apply to it.</summary>
            private static string MemberName(Type type, string parameter) =>
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase))?.Name ?? parameter;

            private static bool IsCopyConstructor(ConstructorInfo ctor, Type type)
            {
                var parameters = ctor.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == type;
            }

            private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        // ------------------------------------------------------------------ nullable reference annotations

        /// <summary>
        /// Whether a reference-type member is declared nullable (string?), read from the compiler's [Nullable] and
        /// [NullableContext] attributes so it also works where NullabilityInfoContext does not exist (netstandard2.0).
        /// </summary>
        private static bool IsNullableReference(Slot slot)
        {
            var flag = Flag(slot.Member!.GetCustomAttributes(false), "System.Runtime.CompilerServices.NullableAttribute");
            if (flag == null && slot.Member is ParameterInfo parameter)
                flag = Flag(parameter.Member.GetCustomAttributes(false), "System.Runtime.CompilerServices.NullableContextAttribute");
            var owner = slot.Member is MemberInfo member ? member.DeclaringType : (slot.Member as ParameterInfo)?.Member.DeclaringType;
            for (var t = owner; flag == null && t != null; t = t.DeclaringType)
                flag = Flag(t.GetCustomAttributes(false), "System.Runtime.CompilerServices.NullableContextAttribute");
            return flag == 2;
        }

        private static byte? Flag(object[] attributes, string name)
        {
            foreach (var attribute in attributes)
            {
                var type = attribute.GetType();
                if (type.FullName != name) continue;
                var value = type.GetField("NullableFlags")?.GetValue(attribute) ?? type.GetField("Flag")?.GetValue(attribute);
                if (value is byte b) return b;
                if (value is byte[] bytes && bytes.Length > 0) return bytes[0];
            }
            return null;
        }
    }
}
