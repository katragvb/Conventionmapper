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
    /// </summary>
    public static class SampleData
    {
        /// <summary>Maximum depth of nested objects; deeper members are left at their default.</summary>
        public const int MaxDepth = 4;

        /// <summary>A new <typeparamref name="T"/> with every settable member filled.</summary>
        public static T Create<T>() => (T)Create(typeof(T))!;

        /// <summary>A new instance of <paramref name="type"/> with every settable member filled, or null when it cannot be built.</summary>
        public static object? Create(Type type) => new Filler().Value(type, type.Name, 0);

        private sealed class Filler
        {
            private readonly HashSet<Type> _building = new HashSet<Type>();
            private int _counter;

            public object? Value(Type type, string name, int depth)
            {
                var underlying = Nullable.GetUnderlyingType(type);
                if (underlying != null) type = underlying;
                var n = ++_counter;

                if (type == typeof(string)) return name + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (type == typeof(object)) return name + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return true;
                if (type == typeof(char)) return (char)('a' + n % 26);
                if (type.IsEnum) return EnumValue(type);
                if (type == typeof(decimal)) return n + 0.5m;
                if (type == typeof(double)) return n + 0.5d;
                if (type == typeof(float)) return n + 0.5f;
                if (type.IsPrimitive) return Convert.ChangeType(n % 100 + 1, type, CultureInfo.InvariantCulture);
                if (type == typeof(Guid)) return new Guid(n, 0, 0, new byte[8]);
                if (type == typeof(DateTime)) return new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n);
                if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(n);
                if (type == typeof(TimeSpan)) return TimeSpan.FromMinutes(n);
                if (type == typeof(Uri)) return new Uri("https://example.com/" + name + "/" + n.ToString(CultureInfo.InvariantCulture));
                if (type.FullName == "System.DateOnly") return Activator.CreateInstance(type, 2020, 1, n % 28 + 1);
                if (type.FullName == "System.TimeOnly") return Activator.CreateInstance(type, n % 24, 0);

                if (depth >= MaxDepth) return Default(type);
                if (type.IsArray) return Array(type, name, depth);
                var collection = Collection(type, name, depth);
                if (collection != null) return collection;
                if (type.IsAbstract || type.IsInterface || type.IsPointer || type.IsByRef || type.ContainsGenericParameters) return Default(type);
                return Object(type, name, depth);
            }

            private static object EnumValue(Type type)
            {
                var values = Enum.GetValues(type).Cast<object>().ToList();
                if (values.Count == 0) return Activator.CreateInstance(type)!;
                // A non-zero value, so it differs from the default.
                return values.FirstOrDefault(v => System.Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0) ?? values[0];
            }

            private object? Array(Type type, string name, int depth)
            {
                var element = type.GetElementType()!;
                var array = System.Array.CreateInstance(element, 1);
                array.SetValue(Value(element, name, depth + 1), 0);
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
                    var key = Value(args[0], name + "Key", depth + 1);
                    if (key == null) return target;
                    var add = target.GetType().GetMethod("Add", new[] { args[0], args[1] });
                    add?.Invoke(target, new[] { key, Value(args[1], name + "Value", depth + 1) });
                    return target;
                }

                var enumerable = type == typeof(string) ? null : FindGeneric(type, typeof(IEnumerable<>));
                if (enumerable == null) return null;
                var elementType = enumerable.GetGenericArguments()[0];
                var isSet = FindGeneric(type, typeof(ISet<>)) != null;
                var created = Instantiate(type, (isSet ? typeof(HashSet<>) : typeof(List<>)).MakeGenericType(elementType));
                if (created == null) return null;
                var item = Value(elementType, name + "Item", depth + 1);
                var addItem = created.GetType().GetMethod("Add", new[] { elementType });
                addItem?.Invoke(created, new[] { item });
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
                    var ctor = type.GetConstructors().Where(c => !IsCopyConstructor(c, type)).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                    var parameterless = type.GetConstructor(Type.EmptyTypes);
                    var filledByConstructor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (ctor != null && ctor.GetParameters().Length > 0)
                    {
                        var parameters = ctor.GetParameters();
                        var args = parameters.Select(p => Value(p.ParameterType, p.Name ?? name, depth + 1)).ToArray();
                        instance = ctor.Invoke(args);
                        foreach (var p in parameters) if (p.Name != null) filledByConstructor.Add(p.Name);
                    }
                    else if (parameterless != null || type.IsValueType)
                    {
                        instance = Activator.CreateInstance(type);
                    }
                    else
                    {
                        return null;
                    }

                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (property.GetIndexParameters().Length > 0 || filledByConstructor.Contains(property.Name)) continue;
                        var setter = property.GetSetMethod(nonPublic: false);
                        if (setter == null) continue;
                        property.SetValue(instance, Value(property.PropertyType, property.Name, depth + 1));
                    }
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (field.IsInitOnly || filledByConstructor.Contains(field.Name)) continue;
                        field.SetValue(instance, Value(field.FieldType, field.Name, depth + 1));
                    }
                    return instance;
                }
                finally
                {
                    _building.Remove(type);
                }
            }

            private static bool IsCopyConstructor(ConstructorInfo ctor, Type type)
            {
                var parameters = ctor.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == type;
            }

            private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
