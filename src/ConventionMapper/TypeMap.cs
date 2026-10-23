using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>A sealed, executable map between two types.</summary>
    public sealed class TypeMap
    {
        private List<PropertyMap> _propertyMaps = new List<PropertyMap>();
        private readonly Dictionary<string, PropertyMap> _propertyMapsByName = new Dictionary<string, PropertyMap>(StringComparer.Ordinal);
        private TypeMap[]? _allDerived;

        internal TypeMap(TypeMapConfiguration config, ProfileMap profile)
        {
            Config = config;
            Profile = profile;
            SourceType = config.SourceType;
            DestinationType = config.DestinationType;
            MaxDepth = config.MaxDepth;
            PreserveReferences = config.PreserveReferences;
        }

        public Type SourceType { get; }
        public Type DestinationType { get; }
        public TypePair Types => new TypePair(SourceType, DestinationType);
        public string ProfileName => Profile.Name;
        public MemberList ConfiguredMemberList => Config.MemberList;
        public int MaxDepth { get; }
        public bool PreserveReferences { get; internal set; }
        public bool HasTypeConverter => TypeConverter != null;
        public IReadOnlyList<PropertyMap> PropertyMaps => _propertyMaps;

        /// <summary>Unmapped members for the configured <see cref="MemberList"/>.</summary>
        public string[] GetUnmappedPropertyNames()
        {
            if (TypeConverter != null) return Array.Empty<string>();
            switch (Config.MemberList)
            {
                case MemberList.Destination: return GetUnmappedDestinationMembers().ToArray();
                case MemberList.Source: return GetUnmappedSourceMembers().ToArray();
                default: return Array.Empty<string>();
            }
        }

        public override string ToString() => SourceType.Name + " -> " + DestinationType.Name;

        // ------------------------------------------------------------------ internal state

        internal TypeMapConfiguration Config { get; }
        internal ProfileMap Profile { get; }
        internal TypeMap? ReverseOf { get; set; }
        internal List<TypeMap> BaseMaps { get; } = new List<TypeMap>();
        internal List<TypeMap> DerivedMaps { get; } = new List<TypeMap>();
        internal bool IsSealed { get; private set; }
        internal bool IsSealing { get; set; }
        internal Func<object, object, bool>? EqualityComparison => Config.EqualityComparison;
        internal bool IgnoreNullSourceValues => Config.IgnoreNullSourceValues || Profile.IgnoreNullSourceValues;

        internal Func<object?, object?, ResolutionContext, object?>? TypeConverter { get; private set; }
        internal LambdaExpression? TypeConverterExpression { get; private set; }
        internal Func<object, ResolutionContext, object>? CustomCtor { get; private set; }
        internal LambdaExpression? CustomCtorExpression { get; private set; }
        internal ConstructorMap? ConstructorMap { get; private set; }
        internal Func<object>? Factory { get; private set; }
        internal IReadOnlyList<DestinationMember> DestinationMembers { get; private set; } = Array.Empty<DestinationMember>();
        internal PropertyMap[] OrderedPropertyMaps { get; private set; } = Array.Empty<PropertyMap>();
        internal PathMap[] PathMaps { get; private set; } = Array.Empty<PathMap>();
        internal Action<object, object, ResolutionContext>[] BeforeMaps { get; private set; } = Array.Empty<Action<object, object, ResolutionContext>>();
        internal Action<object, object, ResolutionContext>[] AfterMaps { get; private set; } = Array.Empty<Action<object, object, ResolutionContext>>();
        internal HashSet<string> CtorMappedNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> PathRoots { get; } = new HashSet<string>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ polymorphism

        internal TypeMap SelectDerived(Type runtimeSourceType, Type requestedDestinationType, object? destination)
        {
            if (DerivedMaps.Count == 0) return this;
            var all = _allDerived ??= CollectDerived();
            var best = this;
            foreach (var candidate in all)
            {
                if (!candidate.SourceType.IsAssignableFrom(runtimeSourceType)) continue;
                if (!requestedDestinationType.IsAssignableFrom(candidate.DestinationType)) continue;
                if (destination != null && !candidate.DestinationType.IsInstanceOfType(destination)) continue;
                if (ReferenceEquals(best, this) || best.SourceType.IsAssignableFrom(candidate.SourceType)) best = candidate;
            }
            return best;
        }

        private TypeMap[] CollectDerived()
        {
            var result = new List<TypeMap>();
            var queue = new Queue<TypeMap>(DerivedMaps);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (result.Contains(next) || ReferenceEquals(next, this)) continue;
                result.Add(next);
                foreach (var d in next.DerivedMaps) queue.Enqueue(d);
            }
            return result.ToArray();
        }

        private List<TypeMap> CollectBases()
        {
            var result = new List<TypeMap>();
            var queue = new Queue<TypeMap>(BaseMaps);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (result.Contains(next) || ReferenceEquals(next, this)) continue;
                result.Add(next);
                foreach (var b in next.BaseMaps) queue.Enqueue(b);
            }
            return result;
        }

        // ------------------------------------------------------------------ sealing

        internal void Seal()
        {
            if (IsSealed) return;
            var config = Config;
            var bases = CollectBases();

            // Member configuration inherited from base maps (Include / IncludeBase)
            var memberConfigs = new Dictionary<string, MemberConfig>(config.Members, StringComparer.Ordinal);
            foreach (var baseMap in bases)
                foreach (var pair in baseMap.Config.Members)
                    if (!memberConfigs.ContainsKey(pair.Key)) memberConfigs[pair.Key] = pair.Value;

            var before = new List<Action<object, object, ResolutionContext>>();
            var after = new List<Action<object, object, ResolutionContext>>();
            for (var i = bases.Count - 1; i >= 0; i--)
            {
                before.AddRange(bases[i].Config.BeforeMaps);
                after.AddRange(bases[i].Config.AfterMaps);
            }
            before.AddRange(config.BeforeMaps);
            after.AddRange(config.AfterMaps);
            BeforeMaps = before.ToArray();
            AfterMaps = after.ToArray();

            DestinationMembers = ReflectionHelper.GetDestinationMembers(DestinationType, Profile.ShouldMapProperty, Profile.ShouldMapField);

            if (config.TypeConverter != null)
            {
                TypeConverter = config.TypeConverter;
                TypeConverterExpression = config.TypeConverterExpression;
                IsSealed = true;
                return;
            }

            CustomCtor = config.CustomCtor;
            CustomCtorExpression = config.CustomCtorExpression;
            Factory = AccessorFactory.CreateFactory(DestinationType);

            var includeChains = new List<MemberInfo[]>();
            foreach (var lambda in config.IncludedMembers)
            {
                var chain = ExpressionHelper.ExtractChain(lambda)
                            ?? throw new MapperConfigurationException(this + ": IncludeMembers expressions must be member chains such as src => src.Inner.");
                includeChains.Add(chain);
            }

            var resolver = Profile.Resolver;
            MemberInfo[]? ResolveName(string name)
            {
                var chain = resolver.Resolve(SourceType, name);
                if (chain != null) return chain;
                foreach (var include in includeChains)
                {
                    var includeType = include.Length == 0 ? SourceType : ReflectionHelper.GetMemberType(include[include.Length - 1]);
                    var sub = resolver.Resolve(includeType, name);
                    if (sub != null) return include.Concat(sub).ToArray();
                }
                return null;
            }

            if (CustomCtor == null && !Profile.DisableConstructorMapping)
                ConstructorMap = SelectConstructor(memberConfigs, ResolveName);

            // Property maps
            var propertyMaps = new List<PropertyMap>();
            foreach (var member in DestinationMembers)
            {
                memberConfigs.TryGetValue(member.Name, out var mc);
                mc ??= AttributeConfig(member);
                PropertyMap map;
                if (mc != null && mc.Ignored)
                {
                    map = new PropertyMap(member, null, mc, true);
                }
                else if (mc != null && mc.Source != null)
                {
                    map = new PropertyMap(member, ResolveSource(mc.Source, ResolveName), mc, false);
                }
                else
                {
                    if (CtorMappedNames.Contains(member.Name)) continue;
                    if (!member.CanWrite && (member.Type.IsValueType || member.Type == typeof(string) || !member.CanRead)) continue;
                    var chain = ResolveName(member.Name);
                    if (chain != null) map = new PropertyMap(member, new ChainSource(chain, SourceType), mc, false);
                    else map = new PropertyMap(member, null, mc, Profile.IsGloballyIgnored(member.Name));
                }

                if (config.IgnoreInaccessibleSetters && !member.HasPublicSetter && !map.Ignored && (mc == null || mc.Source == null))
                    map = new PropertyMap(member, null, mc, true);

                map.Transformers = BuildTransformers(member.Type, mc);
                propertyMaps.Add(map);
            }

            var pathMaps = new List<PathMap>();
            if (ReverseOf != null) GenerateReverseMappings(propertyMaps, pathMaps, memberConfigs);

            foreach (var path in config.Paths)
            {
                pathMaps.RemoveAll(p => p.Path.SequenceEqual(path.Path));
                PathRoots.Add(path.Path[0].Name);
                if (path.Ignored || path.Source == null) continue;
                pathMaps.Add(new PathMap(path.Path, ResolveSource(path.Source, ResolveName), path.Condition));
            }

            _propertyMaps = propertyMaps;
            foreach (var map in propertyMaps) _propertyMapsByName[map.DestinationName] = map;
            OrderedPropertyMaps = propertyMaps.Where(p => !p.Ignored && p.Source != null).OrderBy(p => p.MappingOrder ?? int.MaxValue).ToArray();
            PathMaps = pathMaps.ToArray();
            IsSealed = true;
        }

        private ConstructorMap? SelectConstructor(Dictionary<string, MemberConfig> memberConfigs, Func<string, MemberInfo[]?> resolveName)
        {
            if (DestinationType.IsAbstract || DestinationType.IsInterface) return null;
            var ctors = DestinationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length);
            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0) return null;
                var maps = new CtorParamMap[parameters.Length];
                var ok = true;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    var name = p.Name ?? string.Empty;
                    ValueSource? source = null;
                    if (Config.CtorParams.TryGetValue(name, out var pc) && pc.Source != null)
                    {
                        source = ResolveSource(pc.Source, resolveName);
                    }
                    else
                    {
                        var mc = memberConfigs.Values.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (mc != null && mc.Source != null && !mc.Ignored) source = ResolveSource(mc.Source, resolveName);
                        else if (name.Length > 0)
                        {
                            var chain = resolveName(name);
                            if (chain != null) source = new ChainSource(chain, SourceType);
                        }
                    }
                    if (source == null && !p.HasDefaultValue && !p.IsOptional)
                    {
                        ok = false;
                        break;
                    }
                    maps[i] = new CtorParamMap(p, source);
                }
                if (!ok) continue;
                foreach (var map in maps)
                    if (map.Source != null && map.Parameter.Name != null) CtorMappedNames.Add(map.Parameter.Name);
                return new ConstructorMap(ctor, maps);
            }
            return null;
        }

        private void GenerateReverseMappings(List<PropertyMap> propertyMaps, List<PathMap> pathMaps, Dictionary<string, MemberConfig> memberConfigs)
        {
            foreach (var forward in ReverseOf!._propertyMaps)
            {
                if (forward.Ignored || !(forward.Source is ChainSource chain) || chain.Members.Length == 0) continue;
                if (!forward.DestinationMember.CanRead) continue;
                var members = chain.Members;
                if (members.Any(m => m is MethodInfo)) continue;
                if (memberConfigs.TryGetValue(members[0].Name, out var explicitConfig) && explicitConfig.IsExplicit) continue;

                var reverseSource = new ChainSource(new[] { forward.DestinationMember.Member }, SourceType);
                if (members.Length == 1)
                {
                    if (string.Equals(members[0].Name, forward.DestinationName, StringComparison.OrdinalIgnoreCase)) continue;
                    var index = propertyMaps.FindIndex(p => p.DestinationName == members[0].Name);
                    if (index < 0) continue;
                    var existing = propertyMaps[index];
                    if (existing.Ignored || existing.Source != null) continue;
                    propertyMaps[index] = new PropertyMap(existing.DestinationMember, reverseSource, null, false) { Transformers = existing.Transformers };
                    continue;
                }
                if (!PathMap.IsWritablePath(members)) continue;
                pathMaps.Add(new PathMap(members, reverseSource, null));
                PathRoots.Add(members[0].Name);
            }
        }

        private ValueSource ResolveSource(ValueSource source, Func<string, MemberInfo[]?> resolveName)
        {
            if (source is PathStringSource path) return ResolvePath(path, resolveName);
            if (source is ConverterSource converter && converter.Inner is PathStringSource innerPath)
                return converter.WithInner(ResolvePath(innerPath, resolveName));
            return source;
        }

        private ValueSource ResolvePath(PathStringSource path, Func<string, MemberInfo[]?> resolveName)
        {
            var chain = path.UseConvention ? resolveName(path.Path) : ReflectionHelper.ResolveMemberPath(SourceType, path.Path);
            if (chain == null)
                throw new MapperConfigurationException(this + ": cannot find source member '" + path.Path + "' on " + SourceType.FullName + ".");
            return new ChainSource(chain, SourceType);
        }

        private static MemberConfig? AttributeConfig(DestinationMember member)
        {
            if (member.Member.IsDefined(typeof(IgnoreMapAttribute), true)) return new MemberConfig(member.Name) { Ignored = true };
            var sourceMember = member.Member.GetCustomAttribute<SourceMemberAttribute>(true);
            return sourceMember != null ? new MemberConfig(member.Name) { Source = new PathStringSource(sourceMember.Name, false) } : null;
        }

        private Func<object?, object?>[] BuildTransformers(Type memberType, MemberConfig? mc)
        {
            var all = Profile.ValueTransformers.Concat(Config.ValueTransformers);
            if (mc != null) all = all.Concat(mc.Transformers);
            return all.Where(t => t.ValueType == memberType).Select(t => t.Transform).ToArray();
        }

        // ------------------------------------------------------------------ validation helpers

        internal IEnumerable<string> GetUnmappedDestinationMembers()
        {
            foreach (var member in DestinationMembers)
            {
                if (!member.CanWrite) continue;
                if (_propertyMapsByName.TryGetValue(member.Name, out var map) && map.IsMapped) continue;
                if (CtorMappedNames.Contains(member.Name) || PathRoots.Contains(member.Name)) continue;
                yield return member.Name;
            }
        }

        internal IEnumerable<string> GetUnmappedSourceMembers()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in _propertyMaps)
                if (map.Source != null) used.UnionWith(map.Source.ReferencedSourceMembers);
            if (ConstructorMap != null)
                foreach (var p in ConstructorMap.Parameters)
                    if (p.Source != null) used.UnionWith(p.Source.ReferencedSourceMembers);
            foreach (var path in PathMaps) used.UnionWith(path.Source.ReferencedSourceMembers);

            var members = ReflectionHelper.GetSourceMembers(SourceType, Profile.ShouldMapProperty, Profile.ShouldMapField, _ => false);
            foreach (var member in members)
            {
                if (used.Contains(member.Name) || Config.SourceMembersNotValidated.Contains(member.Name)) continue;
                yield return member.Name;
            }
        }
    }

    /// <summary>How a single destination member is populated.</summary>
    public sealed class PropertyMap
    {
        internal PropertyMap(DestinationMember destinationMember, ValueSource? source, MemberConfig? config, bool ignored)
        {
            DestinationMember = destinationMember;
            Source = source;
            Ignored = ignored;
            if (config != null)
            {
                PreCondition = config.PreCondition;
                Condition = config.Condition;
                HasNullSubstitute = config.HasNullSubstitute;
                NullSubstitute = config.NullSubstitute;
                UseDestinationValue = config.UseDestinationValue;
                MappingOrder = config.MappingOrder;
            }
        }

        public string DestinationName => DestinationMember.Name;
        public Type DestinationType => DestinationMember.Type;
        public MemberInfo DestinationMemberInfo => DestinationMember.Member;
        public bool Ignored { get; }
        public bool IsMapped => Ignored || Source != null;
        public Type? SourceType => Source?.ValueType;
        public IReadOnlyList<MemberInfo> SourceMembers => (Source as ChainSource)?.Members ?? (IReadOnlyList<MemberInfo>)Array.Empty<MemberInfo>();
        public bool HasNullSubstitute { get; }
        public object? NullSubstitute { get; }
        public int? MappingOrder { get; }

        internal DestinationMember DestinationMember { get; }
        internal ValueSource? Source { get; }
        internal Type SourceValueType => Source?.ValueType ?? typeof(object);
        internal Func<object?, object?, ResolutionContext, bool>? PreCondition { get; }
        internal Func<object?, object?, object?, object?, ResolutionContext, bool>? Condition { get; }
        internal bool UseDestinationValue { get; }
        internal Func<object?, object?>[] Transformers { get; set; } = Array.Empty<Func<object?, object?>>();

        public override string ToString() => DestinationName;
    }

    internal sealed class CtorParamMap
    {
        public CtorParamMap(ParameterInfo parameter, ValueSource? source)
        {
            Parameter = parameter;
            Source = source;
            ParameterType = parameter.ParameterType;
            var value = parameter.HasDefaultValue ? parameter.DefaultValue : null;
            if (value is DBNull || value == Missing.Value) value = null;
            if (value != null && ParameterType.IsEnum && !ParameterType.IsInstanceOfType(value)) value = Enum.ToObject(ParameterType, value);
            if (value == null && ParameterType.IsValueType && Nullable.GetUnderlyingType(ParameterType) == null) value = Activator.CreateInstance(ParameterType);
            DefaultValue = value;
        }

        public ParameterInfo Parameter { get; }
        public Type ParameterType { get; }
        public ValueSource? Source { get; }
        public object? DefaultValue { get; }
    }

    internal sealed class ConstructorMap
    {
        public ConstructorMap(ConstructorInfo constructor, CtorParamMap[] parameters)
        {
            Constructor = constructor;
            Parameters = parameters;
        }

        public ConstructorInfo Constructor { get; }
        public CtorParamMap[] Parameters { get; }

        public object Invoke(object?[] args)
        {
            try
            {
                return Constructor.Invoke(args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>Deep destination path (ForPath / unflattening).</summary>
    internal sealed class PathMap
    {
        public PathMap(MemberInfo[] path, ValueSource source, Func<object?, object?, bool>? condition)
        {
            Path = path;
            Source = source;
            Condition = condition;
            Getters = path.Take(path.Length - 1).Select(AccessorFactory.CreateGetter).ToArray();
            Setters = path.Select(AccessorFactory.CreateSetter).ToArray();
            Factories = path.Take(path.Length - 1).Select(m => AccessorFactory.CreateFactory(ReflectionHelper.GetMemberType(m))).ToArray();
            FinalType = ReflectionHelper.GetMemberType(path[path.Length - 1]);
        }

        public MemberInfo[] Path { get; }
        public ValueSource Source { get; }
        public Func<object?, object?, bool>? Condition { get; }
        public Func<object, object?>[] Getters { get; }
        public Action<object, object?>[] Setters { get; }
        public Func<object>?[] Factories { get; }
        public Type FinalType { get; }

        public static bool IsWritablePath(MemberInfo[] members)
        {
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                if (member is PropertyInfo p)
                {
                    if (p.GetGetMethod(true) == null) return false;
                    if (i == members.Length - 1 && p.GetSetMethod(true) == null) return false;
                    if (i < members.Length - 1 && p.PropertyType.IsValueType) return false;
                }
                else if (member is FieldInfo f)
                {
                    if (f.IsInitOnly && i == members.Length - 1) return false;
                    if (i < members.Length - 1 && f.FieldType.IsValueType) return false;
                }
                else return false;
            }
            return true;
        }
    }
}
