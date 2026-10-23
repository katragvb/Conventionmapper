using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConventionMapper.Execution;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>
    /// Marks a member that must never be copied into a destination that is not itself marked [Sensitive]
    /// (password hashes, tokens, personal data, internal notes, ...).
    /// <para>
    /// A map that would copy a sensitive value into an unmarked member fails when the configuration is built
    /// (and is reported while typing by analyzer CM1003, or by the source generator as CM0009). Values of sensitive
    /// members are also redacted in <see cref="ChangeTrackingExtensions.MapWithChanges{TSource, TDestination}(IMapper, TSource, TDestination)"/>.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = true)]
    public sealed class SensitiveAttribute : Attribute
    {
    }
}

namespace ConventionMapper.Internal
{
    /// <summary>Finds maps that would copy a [Sensitive] source member into a destination that is not marked [Sensitive].</summary>
    internal static class SensitiveGuard
    {
        // Matched by name, so the attribute injected by ConventionMapper.SourceGenerator is recognized too.
        private const string AttributeName = "ConventionMapper.SensitiveAttribute";

        private static readonly ConcurrentDictionary<MemberInfo, bool> MemberCache = new ConcurrentDictionary<MemberInfo, bool>();

        public static bool IsSensitive(MemberInfo member) =>
            MemberCache.GetOrAdd(member, m => Attribute.GetCustomAttributes(m, true).Any(a => a.GetType().FullName == AttributeName));

        public static bool IsSensitive(ParameterInfo parameter) =>
            Attribute.GetCustomAttributes(parameter, true).Any(a => a.GetType().FullName == AttributeName);

        public static List<string> FindLeaks(IEnumerable<TypeMap> typeMaps)
        {
            var errors = new List<string>();
            foreach (var typeMap in typeMaps) AddLeaks(typeMap, errors);
            return errors;
        }

        public static void AddLeaks(TypeMap typeMap, List<string> errors)
        {
            foreach (var map in typeMap.OrderedPropertyMaps)
            {
                if (!(map.Source is ChainSource chain)) continue;
                var leaked = chain.Members.FirstOrDefault(IsSensitive);
                if (leaked != null && !IsSensitive(map.DestinationMember.Member))
                    errors.Add(Message(typeMap, map.DestinationName, chain));
            }

            var ctor = typeMap.ConstructorMap;
            if (ctor != null)
            {
                foreach (var parameter in ctor.Parameters)
                {
                    if (!(parameter.Source is ChainSource chain) || !chain.Members.Any(IsSensitive)) continue;
                    if (IsSensitive(parameter.Parameter) || DestinationPropertyIsSensitive(typeMap.DestinationType, parameter.Parameter.Name)) continue;
                    errors.Add(Message(typeMap, parameter.Parameter.Name ?? "?", chain));
                }
            }

            foreach (var path in typeMap.PathMaps)
            {
                if (!(path.Source is ChainSource chain) || !chain.Members.Any(IsSensitive)) continue;
                if (IsSensitive(path.Path[path.Path.Length - 1])) continue;
                errors.Add(Message(typeMap, string.Join(".", path.Path.Select(m => m.Name)), chain));
            }
        }

        private static bool DestinationPropertyIsSensitive(Type type, string? name) =>
            name != null && type.GetMember(name, MemberTypes.Property | MemberTypes.Field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                .Any(IsSensitive);

        private static string Message(TypeMap typeMap, string destination, ChainSource chain) =>
            ReflectionHelper.FriendlyName(typeMap.SourceType) + " -> " + ReflectionHelper.FriendlyName(typeMap.DestinationType) +
            ": '" + destination + "' would receive the [Sensitive] member '" + string.Join(".", chain.Members.Select(m => m.Name)) +
            "'. Ignore it (ForMember(d => d." + destination + ", o => o.Ignore())), or mark '" + destination + "' [Sensitive] if the destination is protected too.";
    }
}
