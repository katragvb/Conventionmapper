using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ConventionMapper.Execution
{
    /// <summary>
    /// Strongly typed collection helpers called from compiled maps: elements are mapped through the element map's
    /// compiled delegate (or copied directly), with no per-element dictionary lookups and no boxing of value types.
    /// Semantics match the interpreter: existing mutable collections are refilled in place, null sources become
    /// empty collections unless AllowNullCollections is set.
    /// </summary>
    internal static class CompiledCollections
    {
        private static readonly string[] IndexNames =
            Enumerable.Range(0, 64).Select(i => "[" + i.ToString(CultureInfo.InvariantCulture) + "]").ToArray();

        private static string IndexName(int index) =>
            index < IndexNames.Length ? IndexNames[index] : "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        public static Func<TSource, ResolutionContext?, TDestination> NestedElement<TSource, TDestination>(CompiledMap map)
            where TDestination : class =>
            (item, context) => item == null ? null! : (TDestination)map(item, null, context);

        public static object? MapCollection<TSource, TDestination>(
            IEnumerable<TSource>? source,
            Func<TSource, ResolutionContext?, TDestination> map,
            object? existing,
            bool isArray,
            bool allowNullCollections,
            ResolutionContext? context,
            string memberName)
        {
            var target = !isArray && !(existing is Array) && existing is ICollection<TDestination> collection && !collection.IsReadOnly
                ? collection
                : null;

            if (source == null)
            {
                if (allowNullCollections) return null;
                if (target != null)
                {
                    target.Clear();
                    return target;
                }
                return isArray ? (object)Array.Empty<TDestination>() : new List<TDestination>();
            }

            context?.PushPath(memberName);
            try
            {
                var items = MapAll(source, map, context);
                if (target != null)
                {
                    target.Clear();
                    foreach (var item in items) target.Add(item);
                    return target;
                }
                return isArray ? (object)items.ToArray() : items;
            }
            catch (MappingException ex) when (context != null)
            {
                ex.CapturePath(context);
                throw;
            }
            finally
            {
                context?.PopPath();
            }
        }

        private static List<TDestination> MapAll<TSource, TDestination>(IEnumerable<TSource> source, Func<TSource, ResolutionContext?, TDestination> map, ResolutionContext? context)
        {
            var result = source is ICollection<TSource> sized ? new List<TDestination>(sized.Count) : new List<TDestination>();
            if (context == null)
            {
                foreach (var item in source) result.Add(map(item, null));
                return result;
            }

            var index = 0;
            foreach (var item in source)
            {
                context.PushPath(IndexName(index));
                try
                {
                    result.Add(map(item, context));
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
            return result;
        }
    }
}
