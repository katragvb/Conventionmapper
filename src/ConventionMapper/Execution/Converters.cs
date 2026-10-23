using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ConventionMapper.Internal;

namespace ConventionMapper.Execution
{
    internal static class CollectionMapper
    {
        public static object Map(MappingEngine engine, IEnumerable source, Type sourceType, Type destinationType, object? existing, ResolutionContext context, ProfileMap profile)
        {
            if (ReflectionHelper.TryGetDictionaryTypes(destinationType, out var destKey, out var destValue))
                return MapDictionary(engine, source, sourceType, destinationType, destKey, destValue, existing, context, profile);

            var destElement = ReflectionHelper.GetElementType(destinationType);
            var sourceElement = ReflectionHelper.GetElementType(sourceType);
            var items = new List<object?>();
            var comparison = existing is IEnumerable && !(existing is Array)
                ? engine.Configuration.ResolveTypeMap(sourceElement, sourceElement, destElement)?.EqualityComparison
                : null;
            List<object?>? pool = null;
            if (comparison != null)
            {
                pool = new List<object?>();
                foreach (var current in (IEnumerable)existing!) pool.Add(current);
            }

            var index = 0;
            foreach (var item in source)
            {
                context.PushPath("[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                try
                {
                    object? target = null;
                    if (pool != null && item != null)
                    {
                        for (var i = 0; i < pool.Count; i++)
                        {
                            var candidate = pool[i];
                            if (candidate == null || !comparison!(item, candidate)) continue;
                            target = candidate;
                            pool.RemoveAt(i);
                            break;
                        }
                    }
                    items.Add(engine.Map(item, sourceElement, destElement, target, context, profile));
                }
                catch (MappingException ex)
                {
                    ex.CapturePath(context);
                    throw;
                }
                finally
                {
                    context.PopPath();
                }
                index++;
            }
            return Materialize(destinationType, destElement, items, existing);
        }

        public static object CreateEmpty(MappingEngine engine, Type destinationType, ResolutionContext context, ProfileMap profile) =>
            Map(engine, Array.Empty<object>(), typeof(object[]), destinationType, null, context, profile);

        public static bool TryClear(object existing)
        {
            if (existing is IDictionary dictionary)
            {
                if (dictionary.IsReadOnly || dictionary.IsFixedSize) return false;
                dictionary.Clear();
                return true;
            }
            return TryRefill(existing, ReflectionHelper.GetElementType(existing.GetType()), new List<object?>());
        }

        private static object Materialize(Type destinationType, Type elementType, List<object?> items, object? existing)
        {
            if (destinationType.IsArray)
            {
                var array = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }
            if (existing != null && TryRefill(existing, elementType, items)) return existing;
            return Create(destinationType, elementType, items);
        }

        private static bool TryRefill(object existing, Type elementType, List<object?> items)
        {
            if (existing is Array) return false;
            if (existing is IList list)
            {
                if (list.IsReadOnly || list.IsFixedSize) return false;
                list.Clear();
                foreach (var item in items) list.Add(item);
                return true;
            }
            var collection = ReflectionHelper.FindGenericInterface(existing.GetType(), typeof(ICollection<>));
            if (collection == null || !collection.GetGenericArguments()[0].IsAssignableFrom(elementType)) return false;
            if ((bool)collection.GetProperty("IsReadOnly")!.GetValue(existing, null)!) return false;
            Invoke(collection.GetMethod("Clear")!, existing, null);
            var add = collection.GetMethod("Add")!;
            foreach (var item in items) Invoke(add, existing, new[] { item });
            return true;
        }

        private static object Create(Type destinationType, Type elementType, List<object?> items)
        {
            if (ImmutableFactory.IsImmutable(destinationType))
            {
                var source = FillList(typeof(List<>).MakeGenericType(elementType), items);
                if (ImmutableFactory.TryCreate(destinationType, new[] { elementType }, source, out var immutable)) return immutable!;
                throw new MappingException("Cannot create an instance of immutable collection type " + destinationType.FullName + ".");
            }

            var listType = typeof(List<>).MakeGenericType(elementType);
            if (destinationType.IsInterface || destinationType.IsAbstract)
            {
                if (destinationType.IsAssignableFrom(listType)) return FillList(listType, items);
                var hashSetType = typeof(HashSet<>).MakeGenericType(elementType);
                if (destinationType.IsAssignableFrom(hashSetType)) return Activator.CreateInstance(hashSetType, FillList(listType, items))!;
                var observableType = typeof(ObservableCollection<>).MakeGenericType(elementType);
                if (destinationType.IsAssignableFrom(observableType)) return Activator.CreateInstance(observableType, FillList(listType, items))!;
                throw new MappingException("Cannot create an instance of collection type " + destinationType.FullName + ".");
            }
            if (destinationType == listType) return FillList(listType, items);
            if (destinationType.IsGenericType && destinationType.GetGenericTypeDefinition() == typeof(ReadOnlyCollection<>))
                return Activator.CreateInstance(destinationType, FillList(listType, items))!;

            var defaultCtor = destinationType.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
            {
                var instance = defaultCtor.Invoke(null);
                if (TryRefill(instance, elementType, items)) return instance;
            }
            var enumerableCtor = destinationType.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(elementType) });
            if (enumerableCtor != null) return enumerableCtor.Invoke(new[] { FillList(listType, items) });
            throw new MappingException("Cannot create an instance of collection type " + destinationType.FullName + ": no parameterless constructor with Add, and no IEnumerable<T> constructor.");
        }

        private static object FillList(Type listType, List<object?> items)
        {
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in items) list.Add(item);
            return list;
        }

        private static object MapDictionary(MappingEngine engine, IEnumerable source, Type sourceType, Type destinationType, Type destKey, Type destValue, object? existing, ResolutionContext context, ProfileMap profile)
        {
            var sourceKey = typeof(object);
            var sourceValue = typeof(object);
            if (ReflectionHelper.TryGetDictionaryTypes(sourceType, out var sk, out var sv))
            {
                sourceKey = sk;
                sourceValue = sv;
            }

            var pairs = new List<KeyValuePair<object, object?>>();
            void Add(object? key, object? value, Type keyType, Type valueType)
            {
                var mappedKey = engine.Map(key, keyType, destKey, null, context, profile)
                                ?? throw new MappingException("Dictionary keys cannot be null when mapping to " + destinationType.FullName + ".");
                pairs.Add(new KeyValuePair<object, object?>(mappedKey, engine.Map(value, valueType, destValue, null, context, profile)));
            }

            if (source is IDictionary nonGeneric)
            {
                foreach (DictionaryEntry entry in nonGeneric) Add(entry.Key, entry.Value, sourceKey, sourceValue);
            }
            else
            {
                foreach (var item in source)
                {
                    if (item == null) continue;
                    var itemType = item.GetType();
                    var keyProperty = itemType.GetProperty("Key");
                    var valueProperty = itemType.GetProperty("Value");
                    if (keyProperty == null || valueProperty == null)
                        throw new MappingException("Cannot map element of type " + itemType.FullName + " to a dictionary entry.");
                    Add(keyProperty.GetValue(item, null), valueProperty.GetValue(item, null), keyProperty.PropertyType, valueProperty.PropertyType);
                }
            }

            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(destKey, destValue);
            if (ImmutableFactory.IsImmutable(destinationType))
            {
                var temp = (IDictionary)Activator.CreateInstance(dictionaryType)!;
                foreach (var pair in pairs) temp[pair.Key] = pair.Value;
                if (ImmutableFactory.TryCreate(destinationType, new[] { destKey, destValue }, temp, out var immutable)) return immutable!;
                throw new MappingException("Cannot create an instance of immutable dictionary type " + destinationType.FullName + ".");
            }

            object target;
            var wrapReadOnly = destinationType.IsGenericType && destinationType.GetGenericTypeDefinition() == typeof(ReadOnlyDictionary<,>);
            if (!wrapReadOnly && existing is IDictionary existingDictionary && !existingDictionary.IsReadOnly && !existingDictionary.IsFixedSize)
            {
                existingDictionary.Clear();
                target = existing;
            }
            else if (wrapReadOnly || destinationType.IsInterface || destinationType.IsAbstract)
            {
                if (!wrapReadOnly && !destinationType.IsAssignableFrom(dictionaryType))
                    throw new MappingException("Cannot create an instance of dictionary type " + destinationType.FullName + ".");
                target = Activator.CreateInstance(dictionaryType)!;
            }
            else
            {
                target = Activator.CreateInstance(destinationType)!;
            }

            if (target is IDictionary dictionary)
            {
                foreach (var pair in pairs) dictionary[pair.Key] = pair.Value;
            }
            else
            {
                var iface = typeof(IDictionary<,>).MakeGenericType(destKey, destValue);
                if (!iface.IsInstanceOfType(target)) throw new MappingException("Cannot populate dictionary type " + destinationType.FullName + ".");
                var indexer = iface.GetProperty("Item")!;
                foreach (var pair in pairs) indexer.SetValue(target, pair.Value, new[] { pair.Key });
            }

            return wrapReadOnly ? Activator.CreateInstance(destinationType, target)! : target;
        }

        private static object? Invoke(MethodInfo method, object? target, object?[]? args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>System.Collections.Immutable support via reflection (no package dependency).</summary>
    internal static class ImmutableFactory
    {
        private const string Namespace = "System.Collections.Immutable";

        public static bool IsImmutable(Type type) => type.IsGenericType && type.Namespace == Namespace;

        public static bool TryCreate(Type destinationType, Type[] typeArguments, object items, out object? result)
        {
            result = null;
            if (!IsImmutable(destinationType)) return false;
            var definition = destinationType.GetGenericTypeDefinition();
            var name = definition.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            if (definition.IsInterface)
            {
                switch (name)
                {
                    case "IImmutableList": name = "ImmutableList"; break;
                    case "IImmutableSet": name = "ImmutableHashSet"; break;
                    case "IImmutableStack": name = "ImmutableStack"; break;
                    case "IImmutableQueue": name = "ImmutableQueue"; break;
                    case "IImmutableDictionary": name = "ImmutableDictionary"; break;
                    default: return false;
                }
            }
            var factory = definition.Assembly.GetType(Namespace + "." + name);
            var method = factory?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                m.Name == "CreateRange" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == typeArguments.Length && m.GetParameters().Length == 1);
            if (method == null) return false;
            try
            {
                result = method.MakeGenericMethod(typeArguments).Invoke(null, new[] { items });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return result != null && destinationType.IsInstanceOfType(result);
        }
    }

    internal static class EnumMapper
    {
        public static object ToEnum(object source, Type sourceType, Type enumType)
        {
            if (source is string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return Activator.CreateInstance(enumType)!;
                return Enum.Parse(enumType, text.Trim(), true);
            }
            if (sourceType.IsEnum)
            {
                if (sourceType == enumType) return source;
                var name = source.ToString();
                if (!string.IsNullOrEmpty(name) && !char.IsDigit(name[0]) && name[0] != '-')
                {
                    try
                    {
                        return Enum.Parse(enumType, name, true);
                    }
                    catch (ArgumentException)
                    {
                        // fall back to the underlying value
                    }
                }
                return Enum.ToObject(enumType, Convert.ToInt64(source, CultureInfo.InvariantCulture));
            }
            if (ReflectionHelper.IsIntegral(sourceType)) return Enum.ToObject(enumType, source);
            if (source is IConvertible) return Enum.ToObject(enumType, Convert.ToInt64(source, CultureInfo.InvariantCulture));
            throw MappingException.MissingMap(sourceType, enumType);
        }
    }

    /// <summary>Primitive conversions: IConvertible, static Parse, implicit/explicit operators, TypeConverter.</summary>
    internal static class Conversions
    {
        private static readonly ConcurrentDictionary<TypePair, Func<object, object?>?> Cache = new ConcurrentDictionary<TypePair, Func<object, object?>?>();

        public static Func<object, object?>? Get(Type source, Type destination) =>
            Cache.GetOrAdd(new TypePair(source, destination), p => Build(p.SourceType, p.DestinationType));

        private static bool IsConvertibleTarget(Type t) => t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(string);

        private static Func<object, object?>? Build(Type source, Type destination)
        {
            if (IsConvertibleTarget(destination) && typeof(IConvertible).IsAssignableFrom(source))
                return value => Convert.ChangeType(value, destination, CultureInfo.InvariantCulture);

            if (source == typeof(string))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                var parseWithProvider = destination.GetMethod("Parse", flags, null, new[] { typeof(string), typeof(IFormatProvider) }, null);
                if (parseWithProvider != null && destination.IsAssignableFrom(parseWithProvider.ReturnType))
                    return value => Invoke(parseWithProvider, new[] { value, CultureInfo.InvariantCulture });
                var parse = destination.GetMethod("Parse", flags, null, new[] { typeof(string) }, null);
                if (parse != null && destination.IsAssignableFrom(parse.ReturnType))
                    return value => Invoke(parse, new[] { value });
            }

            var op = FindOperator(source, destination, "op_Implicit") ?? FindOperator(source, destination, "op_Explicit");
            if (op != null) return value => Invoke(op, new[] { value });

            try
            {
                var destinationConverter = TypeDescriptor.GetConverter(destination);
                if (destinationConverter.GetType() != typeof(TypeConverter) && destinationConverter.CanConvertFrom(source))
                    return value => destinationConverter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                var sourceConverter = TypeDescriptor.GetConverter(source);
                if (sourceConverter.GetType() != typeof(TypeConverter) && sourceConverter.CanConvertTo(destination))
                    return value => sourceConverter.ConvertTo(null, CultureInfo.InvariantCulture, value, destination);
            }
            catch (Exception)
            {
                // TypeDescriptor is best-effort
            }
            return null;
        }

        private static MethodInfo? FindOperator(Type source, Type destination, string name)
        {
            foreach (var type in new[] { source, destination })
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != name || method.ReturnType != destination) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(source)) return method;
                }
            }
            return null;
        }

        private static object? Invoke(MethodInfo method, object?[] args)
        {
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
