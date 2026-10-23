using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ConventionMapper.Internal
{
    /// <summary>Effective (inherited from root) settings for one profile.</summary>
    internal sealed class ProfileMap
    {
        public ProfileMap(Profile profile, ProfileMap? root)
        {
            Profile = profile;
            Name = profile.ProfileName;
            AllowNullCollections = profile.AllowNullCollectionsValue ?? root?.AllowNullCollections ?? false;
            AllowNullDestinationValues = profile.AllowNullDestinationValuesValue ?? root?.AllowNullDestinationValues ?? true;
            DisableConstructorMapping = profile.DisableConstructorMappingValue ?? root?.DisableConstructorMapping ?? false;
            IgnoreNullSourceValues = profile.IgnoreNullSourceValuesValue ?? root?.IgnoreNullSourceValues ?? false;
            ShouldMapProperty = profile.ShouldMapPropertyValue ?? root?.ShouldMapProperty ?? (_ => true);
            ShouldMapField = profile.ShouldMapFieldValue ?? root?.ShouldMapField ?? (f => f.IsPublic);
            ShouldMapMethod = profile.ShouldMapMethodValue ?? root?.ShouldMapMethod ?? (_ => true);
            SourceConvention = profile.SourceConventionValue ?? root?.SourceConvention ?? PascalCaseNamingConvention.Instance;
            DestinationConvention = profile.DestinationConventionValue ?? root?.DestinationConvention ?? PascalCaseNamingConvention.Instance;

            var inheritPrefixes = root != null && !profile.PrefixesCleared;
            Prefixes = Merge(profile.PrefixList, inheritPrefixes ? root!.Prefixes : null);
            Postfixes = Merge(profile.PostfixList, root?.Postfixes);
            DestinationPrefixes = Merge(profile.DestinationPrefixList, root?.DestinationPrefixes);
            DestinationPostfixes = Merge(profile.DestinationPostfixList, root?.DestinationPostfixes);
            MemberNameReplacers = (root?.MemberNameReplacers ?? Array.Empty<KeyValuePair<string, string>>()).Concat(profile.MemberNameReplacers).ToArray();
            GlobalIgnores = Merge(profile.GlobalIgnores, root?.GlobalIgnores);
            ValueTransformers = (root?.ValueTransformers ?? Array.Empty<ValueTransformerConfiguration>()).Concat(profile.ValueTransformers).ToArray();
            Resolver = new NameResolver(this);
        }

        public Profile Profile { get; }
        public string Name { get; }
        public bool AllowNullCollections { get; }
        public bool AllowNullDestinationValues { get; }
        public bool DisableConstructorMapping { get; }
        public bool IgnoreNullSourceValues { get; }
        public Func<PropertyInfo, bool> ShouldMapProperty { get; }
        public Func<FieldInfo, bool> ShouldMapField { get; }
        public Func<MethodInfo, bool> ShouldMapMethod { get; }
        public INamingConvention SourceConvention { get; }
        public INamingConvention DestinationConvention { get; }
        public string[] Prefixes { get; }
        public string[] Postfixes { get; }
        public string[] DestinationPrefixes { get; }
        public string[] DestinationPostfixes { get; }
        public KeyValuePair<string, string>[] MemberNameReplacers { get; }
        public string[] GlobalIgnores { get; }
        public ValueTransformerConfiguration[] ValueTransformers { get; }
        public NameResolver Resolver { get; }

        public bool IsGloballyIgnored(string name)
        {
            foreach (var prefix in GlobalIgnores)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string[] Merge(IEnumerable<string> own, IEnumerable<string>? inherited) =>
            own.Concat(inherited ?? Array.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Matches destination member names to source members: exact (case-insensitive) match, naming-convention
    /// translation, recognized prefixes/postfixes, and flattening (CustomerName → Customer.Name).
    /// </summary>
    internal sealed class NameResolver
    {
        private const int MaxFlatteningDepth = 8;
        private readonly ProfileMap _profile;
        private readonly ConcurrentDictionary<Type, Dictionary<string, MemberInfo>> _indexes = new ConcurrentDictionary<Type, Dictionary<string, MemberInfo>>();

        public NameResolver(ProfileMap profile) { _profile = profile; }

        public MemberInfo[]? Resolve(Type sourceType, string destinationName)
        {
            foreach (var candidate in DestinationNameCandidates(destinationName))
            {
                var words = _profile.DestinationConvention.Split(candidate);
                var result = ResolveWords(sourceType, words, 0, 0);
                if (result != null) return result;
            }
            return null;
        }

        private IEnumerable<string> DestinationNameCandidates(string name)
        {
            yield return name;
            foreach (var prefix in _profile.DestinationPrefixes)
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)) yield return name.Substring(prefix.Length);
            foreach (var postfix in _profile.DestinationPostfixes)
                if (name.Length > postfix.Length && name.EndsWith(postfix, StringComparison.Ordinal)) yield return name.Substring(0, name.Length - postfix.Length);
        }

        private MemberInfo[]? ResolveWords(Type type, string[] words, int start, int depth)
        {
            if (depth > MaxFlatteningDepth || start >= words.Length) return null;
            var index = _indexes.GetOrAdd(type, BuildIndex);

            if (index.TryGetValue(Join(words, start, words.Length), out var direct)) return new[] { direct };

            for (var end = start + 1; end < words.Length; end++)
            {
                if (!index.TryGetValue(Join(words, start, end), out var member)) continue;
                var rest = ResolveWords(ReflectionHelper.GetMemberType(member), words, end, depth + 1);
                if (rest == null) continue;
                var result = new MemberInfo[rest.Length + 1];
                result[0] = member;
                Array.Copy(rest, 0, result, 1, rest.Length);
                return result;
            }
            return null;
        }

        private static string Join(string[] words, int start, int end)
        {
            if (end - start == 1) return words[start].ToLowerInvariant();
            return string.Concat(words.Skip(start).Take(end - start)).ToLowerInvariant();
        }

        private Dictionary<string, MemberInfo> BuildIndex(Type type)
        {
            var members = ReflectionHelper.GetSourceMembers(type, _profile.ShouldMapProperty, _profile.ShouldMapField, _profile.ShouldMapMethod);
            var index = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);

            foreach (var member in members)
            {
                var key = Key(ApplyReplacements(member.Name));
                if (!index.ContainsKey(key)) index[key] = member;
            }
            foreach (var member in members)
            {
                var name = ApplyReplacements(member.Name);
                foreach (var prefix in _profile.Prefixes)
                {
                    if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var key = Key(name.Substring(prefix.Length));
                    if (!index.ContainsKey(key)) index[key] = member;
                }
                foreach (var postfix in _profile.Postfixes)
                {
                    if (name.Length <= postfix.Length || !name.EndsWith(postfix, StringComparison.Ordinal)) continue;
                    var key = Key(name.Substring(0, name.Length - postfix.Length));
                    if (!index.ContainsKey(key)) index[key] = member;
                }
            }
            return index;
        }

        private string ApplyReplacements(string name)
        {
            foreach (var replacer in _profile.MemberNameReplacers)
                name = name.Replace(replacer.Key, replacer.Value);
            return name;
        }

        private string Key(string name) => string.Concat(_profile.SourceConvention.Split(name)).ToLowerInvariant();
    }
}
