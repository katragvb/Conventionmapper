using System;
using System.Linq;
using System.Reflection;

namespace ConventionMapper.Internal
{
    /// <summary>
    /// Opt-in check (cfg.ValidateNullability = true) for members whose source can be null while the destination cannot.
    /// Nullable value types (int? → int) are detected on every target framework; nullable reference type annotations
    /// (string? → string) are read with NullabilityInfoContext on .NET 6+.
    /// </summary>
    internal static class NullabilityHelper
    {
#if NET6_0_OR_GREATER
        [ThreadStatic] private static NullabilityInfoContext? _context;
        private static NullabilityInfoContext Context => _context ??= new NullabilityInfoContext();
#endif

        public static string? Check(TypeMap typeMap, PropertyMap map)
        {
            if (map.Ignored || map.HasNullSubstitute || map.Condition != null || map.PreCondition != null) return null;
            if (!(map.Source is ChainSource chain) || chain.Members.Length == 0) return null;

            var destination = map.DestinationMember;
            if (ReflectionHelper.IsCollectionType(destination.Type) && !typeMap.Profile.AllowNullCollections) return null; // null becomes empty
            if (destination.Type == typeof(string) && !typeMap.Profile.AllowNullDestinationValues) return null;          // null becomes ""
            if (!IsNonNullable(destination.Member)) return null;

            var nullable = chain.Members.FirstOrDefault(MayBeNull);
            if (nullable == null) return null;

            var path = string.Join(".", chain.Members.Select(m => m.Name));
            return typeMap + ", member '" + map.DestinationName + "': source '" + path + "' may be null ('" + nullable.Name +
                   "' is nullable) but the destination is not nullable. Add NullSubstitute(...), a Condition, Ignore(), or make the destination nullable.";
        }

        public static bool MayBeNull(MemberInfo member)
        {
            var type = ReflectionHelper.GetMemberType(member);
            if (type.IsValueType) return Nullable.GetUnderlyingType(type) != null;
#if NET6_0_OR_GREATER
            NullabilityInfo? info = member switch
            {
                PropertyInfo property => Context.Create(property),
                FieldInfo field => Context.Create(field),
                MethodInfo method => Context.Create(method.ReturnParameter),
                _ => null
            };
            return info != null && info.ReadState == NullabilityState.Nullable;
#else
            return false;
#endif
        }

        public static bool IsNonNullable(MemberInfo member)
        {
            var type = ReflectionHelper.GetMemberType(member);
            if (type.IsValueType) return Nullable.GetUnderlyingType(type) == null;
#if NET6_0_OR_GREATER
            NullabilityInfo? info = member switch
            {
                PropertyInfo property => Context.Create(property),
                FieldInfo field => Context.Create(field),
                _ => null
            };
            return info != null && info.WriteState == NullabilityState.NotNull;
#else
            return false;
#endif
        }
    }
}
