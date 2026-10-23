using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using ConventionMapper.Configuration;
using ConventionMapper.Execution;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    public interface IConfigurationProvider
    {
        void AssertConfigurationIsValid();
        void AssertConfigurationIsValid(string profileName);
        void AssertConfigurationIsValid<TProfile>() where TProfile : Profile, new();
        IMapper CreateMapper();
        IMapper CreateMapper(Func<Type, object> serviceCtor);
        TypeMap? FindTypeMapFor(Type sourceType, Type destinationType);
        TypeMap? FindTypeMapFor<TSource, TDestination>();
        IReadOnlyCollection<TypeMap> GetAllTypeMaps();
        LambdaExpression BuildProjection(Type sourceType, Type destinationType);
        Expression<Func<TSource, TDestination>> BuildProjection<TSource, TDestination>();

        /// <summary>Projection with query parameters (values for variables captured in MapFrom expressions) and explicitly expanded members.</summary>
        LambdaExpression BuildProjection(Type sourceType, Type destinationType, object? parameters, params string[] membersToExpand);
        void CompileMappings();

        /// <summary>Human-readable description of how every destination member of a map is populated.</summary>
        string Explain(Type sourceType, Type destinationType);
        string Explain<TSource, TDestination>();
    }

    /// <summary>Immutable, thread-safe mapping configuration. Create once at startup.</summary>
    public sealed class MapperConfiguration : IConfigurationProvider
    {
        private readonly ConcurrentDictionary<TypePair, TypeMap> _typeMaps = new ConcurrentDictionary<TypePair, TypeMap>();
        private readonly List<OpenGenericMap> _openGenericMaps = new List<OpenGenericMap>();
        private readonly ConcurrentDictionary<(Type, Type, Type), TypeMap?> _resolved = new ConcurrentDictionary<(Type, Type, Type), TypeMap?>();
        private readonly ConcurrentDictionary<TypePair, LambdaExpression> _projections = new ConcurrentDictionary<TypePair, LambdaExpression>();
        private readonly ConcurrentDictionary<(TypePair, string), LambdaExpression> _expandedProjections = new ConcurrentDictionary<(TypePair, string), LambdaExpression>();
        private readonly List<ProfileMap> _profiles = new List<ProfileMap>();
        private readonly object _sync = new object();

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public MapperConfiguration(Action<IMapperConfigurationExpression> configure) : this(CreateExpression(configure)) { }

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public MapperConfiguration(MapperConfigurationExpression configurationExpression)
        {
            if (configurationExpression == null) throw new ArgumentNullException(nameof(configurationExpression));
            ServiceCtor = configurationExpression.ServiceCtorValue;
            UseCompiledMappings = configurationExpression.UseCompiledMappings;
            UseContextPooling = configurationExpression.UseContextPooling;
            ValidateNullability = configurationExpression.ValidateNullability;
            RootProfile = new ProfileMap(configurationExpression, null);
            _profiles.Add(RootProfile);
            foreach (var profile in configurationExpression.Profiles) _profiles.Add(new ProfileMap(profile, RootProfile));
            Engine = new MappingEngine(this);
            Build();
        }

        internal ProfileMap RootProfile { get; }
        internal Func<Type, object>? ServiceCtor { get; }
        internal bool UseCompiledMappings { get; }
        internal bool UseContextPooling { get; }
        internal bool ValidateNullability { get; }
        internal MappingEngine Engine { get; }

        private static MapperConfigurationExpression CreateExpression(Action<IMapperConfigurationExpression> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var expression = new MapperConfigurationExpression();
            configure(expression);
            return expression;
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            var byOriginal = new Dictionary<TypeMapConfiguration, TypeMap>();
            foreach (var profile in _profiles)
            {
                foreach (var original in profile.Profile.TypeMapConfigs)
                {
                    var config = original.Clone();
                    if (config.SourceType.IsGenericTypeDefinition || config.DestinationType.IsGenericTypeDefinition)
                    {
                        _openGenericMaps.Add(new OpenGenericMap(config, profile));
                        continue;
                    }
                    var typeMap = new TypeMap(config, profile);
                    if (!_typeMaps.TryAdd(typeMap.Types, typeMap))
                        throw new MapperConfigurationException("Duplicate CreateMap for " + typeMap.Types + " (profile '" + profile.Name + "'). Each type pair can only be configured once (ReverseMap counts as a CreateMap).");
                    byOriginal[original] = typeMap;
                }
            }

            foreach (var typeMap in _typeMaps.Values.ToList())
            {
                foreach (var action in RootProfile.Profile.AllMapsActions) action(typeMap, new MappingExpression(typeMap.Config, null));
                if (!ReferenceEquals(typeMap.Profile, RootProfile))
                    foreach (var action in typeMap.Profile.Profile.AllMapsActions) action(typeMap, new MappingExpression(typeMap.Config, null));
            }

            foreach (var typeMap in _typeMaps.Values)
            {
                if (typeMap.Config.ReverseOf != null && byOriginal.TryGetValue(typeMap.Config.ReverseOf, out var forward)) typeMap.ReverseOf = forward;
                foreach (var pair in typeMap.Config.IncludedDerived) Link(typeMap, GetIncluded(typeMap, pair, "Include"));
                foreach (var pair in typeMap.Config.IncludedBase) Link(GetIncluded(typeMap, pair, "IncludeBase"), typeMap);
            }

            foreach (var typeMap in _typeMaps.Values) Seal(typeMap);
            foreach (var typeMap in _typeMaps.Values)
                if (!typeMap.PreserveReferences && typeMap.TypeConverter == null && IsReachable(typeMap, typeMap, new HashSet<TypeMap>()))
                    typeMap.PreserveReferences = true;
        }

        private TypeMap GetIncluded(TypeMap owner, TypePair pair, string kind)
        {
            if (!_typeMaps.TryGetValue(pair, out var included))
                throw new MapperConfigurationException(owner + ": " + kind + "<" + pair.SourceType.Name + ", " + pair.DestinationType.Name + ">() requires a matching CreateMap.");
            return included;
        }

        private static void Link(TypeMap baseMap, TypeMap derived)
        {
            if (!baseMap.SourceType.IsAssignableFrom(derived.SourceType) || !baseMap.DestinationType.IsAssignableFrom(derived.DestinationType))
                throw new MapperConfigurationException(derived + " cannot be included in " + baseMap + ": types are not derived from the base map's types.");
            if (!baseMap.DerivedMaps.Contains(derived)) baseMap.DerivedMaps.Add(derived);
            if (!derived.BaseMaps.Contains(baseMap)) derived.BaseMaps.Add(baseMap);
        }

        private static void Seal(TypeMap typeMap)
        {
            if (typeMap.IsSealed || typeMap.IsSealing) return;
            typeMap.IsSealing = true;
            if (typeMap.ReverseOf != null) Seal(typeMap.ReverseOf);
            typeMap.Seal();
            typeMap.IsSealing = false;
        }

        private bool IsReachable(TypeMap from, TypeMap target, HashSet<TypeMap> visited)
        {
            foreach (var child in GetChildMaps(from))
            {
                if (ReferenceEquals(child, target)) return true;
                if (visited.Add(child) && IsReachable(child, target, visited)) return true;
            }
            return false;
        }

        private IEnumerable<TypeMap> GetChildMaps(TypeMap typeMap)
        {
            var pairs = new List<(Type, Type)>();
            foreach (var map in typeMap.PropertyMaps)
                if (!map.Ignored && map.Source != null) pairs.Add((map.SourceValueType, map.DestinationType));
            if (typeMap.ConstructorMap != null)
                foreach (var p in typeMap.ConstructorMap.Parameters)
                    if (p.Source != null) pairs.Add((p.Source.ValueType, p.ParameterType));
            foreach (var path in typeMap.PathMaps) pairs.Add((path.Source.ValueType, path.FinalType));

            foreach (var (source, destination) in pairs)
            {
                var s = source;
                var d = destination;
                if (ReflectionHelper.IsCollectionType(s) && ReflectionHelper.IsCollectionType(d))
                {
                    s = ReflectionHelper.GetElementType(s);
                    d = ReflectionHelper.GetElementType(d);
                }
                var child = ResolveTypeMap(s, s, d);
                if (child == null) continue;
                yield return child;
                foreach (var derived in child.DerivedMaps) yield return derived;
            }
        }

        // ------------------------------------------------------------------ lookup

        internal TypeMap? ResolveTypeMap(Type runtimeType, Type declaredType, Type destinationType)
        {
            var key = (runtimeType, declaredType, destinationType);
            if (_resolved.TryGetValue(key, out var cached)) return cached;

            var found = FindTypeMapCore(runtimeType, destinationType)
                        ?? (declaredType != runtimeType ? FindTypeMapCore(declaredType, destinationType) : null)
                        ?? FindByBaseTypes(runtimeType, destinationType);
            if (found == null)
            {
                var underlying = Nullable.GetUnderlyingType(destinationType);
                if (underlying != null) found = ResolveTypeMap(runtimeType, declaredType, underlying);
            }
            _resolved.TryAdd(key, found);
            return found;
        }

        private TypeMap? FindByBaseTypes(Type type, Type destinationType)
        {
            for (var t = type.BaseType; t != null && t != typeof(object); t = t.BaseType)
            {
                var map = FindTypeMapCore(t, destinationType);
                if (map != null) return map;
            }
            foreach (var iface in type.GetInterfaces())
            {
                var map = FindTypeMapCore(iface, destinationType);
                if (map != null) return map;
            }
            return null;
        }

        internal TypeMap? FindTypeMapCore(Type sourceType, Type destinationType)
        {
            var pair = new TypePair(sourceType, destinationType);
            if (_typeMaps.TryGetValue(pair, out var typeMap)) return typeMap;
            if (_openGenericMaps.Count == 0 || (!sourceType.IsGenericType && !destinationType.IsGenericType)) return null;

            foreach (var open in _openGenericMaps)
            {
                if (!Matches(open.Config.SourceType, sourceType) || !Matches(open.Config.DestinationType, destinationType)) continue;
                lock (_sync)
                {
                    if (_typeMaps.TryGetValue(pair, out typeMap)) return typeMap;
                    var closed = new TypeMap(open.Config.CloseGeneric(sourceType, destinationType), open.Profile);
                    _typeMaps[pair] = closed;
                    closed.Seal();
                    if (!closed.PreserveReferences && closed.TypeConverter == null && IsReachable(closed, closed, new HashSet<TypeMap>()))
                        closed.PreserveReferences = true;
                    return closed;
                }
            }
            return null;
        }

        private static bool Matches(Type configured, Type candidate) =>
            configured.IsGenericTypeDefinition
                ? candidate.IsGenericType && candidate.GetGenericTypeDefinition() == configured
                : configured == candidate;

        // ------------------------------------------------------------------ IConfigurationProvider

        public TypeMap? FindTypeMapFor(Type sourceType, Type destinationType) => FindTypeMapCore(sourceType, destinationType);
        public TypeMap? FindTypeMapFor<TSource, TDestination>() => FindTypeMapCore(typeof(TSource), typeof(TDestination));
        public IReadOnlyCollection<TypeMap> GetAllTypeMaps() => _typeMaps.Values.ToList();

        public IMapper CreateMapper() => new Mapper(this);
        public IMapper CreateMapper(Func<Type, object> serviceCtor) => new Mapper(this, serviceCtor);

        /// <summary>Compiles every compilable map now instead of on first use (call at startup to avoid first-call latency).</summary>
        public void CompileMappings()
        {
            if (!UseCompiledMappings) return;
            foreach (var typeMap in _typeMaps.Values) typeMap.GetCompiledPlan(Engine.Compiler);
        }

        /// <summary>True when a runtime subtype of sourceType could select a different map to destinationType.</summary>
        internal bool HasPotentialSubtypeMaps(Type sourceType, Type destinationType)
        {
            if (sourceType.IsValueType || sourceType.IsSealed) return false;
            if (_openGenericMaps.Count > 0) return true; // closed lazily; be conservative
            foreach (var map in _typeMaps.Values)
                if (map.DestinationType == destinationType && map.SourceType != sourceType && sourceType.IsAssignableFrom(map.SourceType)) return true;
            return false;
        }

        // ------------------------------------------------------------------ static fast-path caches

        internal void TryPopulateFastCache<TSource, TDestination>()
        {
            var current = FastMapCache<TSource, TDestination>.Entry;
            if (current != null && ReferenceEquals(current.Configuration, this)) return;
            FastMapCache<TSource, TDestination>.Entry = CreateFastEntry(typeof(TSource), typeof(TDestination));
        }

        internal void TryPopulateFastCacheForSource<TDestination>(Type sourceType)
        {
            foreach (var existing in FastMapCache<TDestination>.Entries)
                if (existing.SourceType == sourceType && ReferenceEquals(existing.Configuration, this)) return;

            var entry = CreateFastEntry(sourceType, typeof(TDestination));
            lock (FastMapCache<TDestination>.Sync)
            {
                var entries = new List<FastMapEntry>(FastMapCache<TDestination>.Entries);
                if (entries.Exists(e => e.SourceType == sourceType && ReferenceEquals(e.Configuration, this))) return;
                if (entries.Count >= 8) entries.RemoveAll(e => !ReferenceEquals(e.Configuration, this));
                if (entries.Count >= 8) return; // further source types use the normal path
                entries.Add(entry);
                FastMapCache<TDestination>.Entries = entries.ToArray();
            }
        }

        private FastMapEntry CreateFastEntry(Type sourceType, Type destinationType)
        {
            CompiledMap? map = null, contextMap = null;
            TypeMap? found = null;
            if (UseCompiledMappings &&
                _typeMaps.TryGetValue(new TypePair(sourceType, destinationType), out var typeMap) &&
                typeMap.DerivedMaps.Count == 0 && typeMap.TypeConverter == null)
            {
                var plan = typeMap.GetCompiledPlan(Engine.Compiler);
                if (plan != null && plan.ContextFree) map = plan.FastMap ?? plan.Map;
                else if (plan != null)
                {
                    contextMap = plan.Map;
                    found = typeMap;
                }
            }
            return new FastMapEntry(this, sourceType, map, contextMap, found);
        }

        public LambdaExpression BuildProjection(Type sourceType, Type destinationType) =>
            _projections.GetOrAdd(new TypePair(sourceType, destinationType), p => new ProjectionBuilder(this).Build(p.SourceType, p.DestinationType));

        public LambdaExpression BuildProjection(Type sourceType, Type destinationType, object? parameters, params string[] membersToExpand)
        {
            var expand = (membersToExpand ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var lambda = expand.Length == 0
                ? BuildProjection(sourceType, destinationType)
                : _expandedProjections.GetOrAdd((new TypePair(sourceType, destinationType), string.Join("|", expand)),
                    key => new ProjectionBuilder(this, expand).Build(sourceType, destinationType));
            return parameters == null ? lambda : ProjectionParameters.Bind(lambda, parameters);
        }

        public Expression<Func<TSource, TDestination>> BuildProjection<TSource, TDestination>() =>
            (Expression<Func<TSource, TDestination>>)BuildProjection(typeof(TSource), typeof(TDestination));

        public string Explain<TSource, TDestination>() => Explain(typeof(TSource), typeof(TDestination));

        public string Explain(Type sourceType, Type destinationType)
        {
            var typeMap = FindTypeMapCore(sourceType, destinationType)
                          ?? throw new ArgumentException("No map is configured for " + ReflectionHelper.FriendlyName(sourceType) + " -> " + ReflectionHelper.FriendlyName(destinationType) + ".");
            return MapExplainer.Explain(typeMap, this);
        }

        public void AssertConfigurationIsValid() => new ConfigurationValidator(this).Validate(_typeMaps.Values.ToList());

        public void AssertConfigurationIsValid(string profileName)
        {
            var maps = _typeMaps.Values.Where(t => t.ProfileName == profileName).ToList();
            if (maps.Count == 0 && _profiles.All(p => p.Name != profileName))
                throw new ArgumentOutOfRangeException(nameof(profileName), "No profile named '" + profileName + "'.");
            new ConfigurationValidator(this).Validate(maps);
        }

        public void AssertConfigurationIsValid<TProfile>() where TProfile : Profile, new() => AssertConfigurationIsValid(new TProfile().ProfileName);

        private sealed class OpenGenericMap
        {
            public OpenGenericMap(TypeMapConfiguration config, ProfileMap profile)
            {
                Config = config;
                Profile = profile;
            }

            public TypeMapConfiguration Config { get; }
            public ProfileMap Profile { get; }
        }
    }

    internal sealed class ConfigurationValidator
    {
        private readonly MapperConfiguration _configuration;

        public ConfigurationValidator(MapperConfiguration configuration) { _configuration = configuration; }

        public void Validate(IReadOnlyList<TypeMap> typeMaps)
        {
            var errors = new List<string>();
            foreach (var typeMap in typeMaps)
            {
                foreach (var error in typeMap.Config.ValidationErrors) errors.Add(typeMap + ": " + error);
                if (typeMap.HasTypeConverter) continue;

                var unmapped = typeMap.GetUnmappedPropertyNames();
                if (unmapped.Length > 0)
                {
                    var side = typeMap.ConfiguredMemberList == MemberList.Source ? "source" : "destination";
                    errors.Add("Unmapped " + side + " members found for " + typeMap + " (" + typeMap.SourceType.FullName + " -> " + typeMap.DestinationType.FullName + ", profile '" + typeMap.ProfileName + "'):" +
                               Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", unmapped) +
                               Environment.NewLine + "Add a custom mapping expression, ignore, add a custom resolver, or modify the source/destination type.");
                }

                if (typeMap.CustomCtor == null && typeMap.ConstructorMap == null && typeMap.Factory == null && !typeMap.Config.DisableCtorValidation &&
                    !(typeMap.DestinationType.IsAbstract && typeMap.DerivedMaps.Count > 0))
                    errors.Add(typeMap + ": " + typeMap.DestinationType.FullName + " has no usable constructor. Add a parameterless constructor, make constructor parameters resolvable (ForCtorParam), or use ConstructUsing.");

                foreach (var map in typeMap.PropertyMaps)
                {
                    if (map.Ignored || map.Source == null) continue;
                    if (!CanMap(map.SourceValueType, map.DestinationType, new HashSet<TypePair>()))
                        errors.Add(typeMap + ", member '" + map.DestinationName + "': cannot map " + map.SourceValueType.FullName + " to " + map.DestinationType.FullName + ". Create a map for these types or configure the member.");
                }

                if (_configuration.ValidateNullability)
                {
                    foreach (var map in typeMap.PropertyMaps)
                    {
                        var problem = NullabilityHelper.Check(typeMap, map);
                        if (problem != null) errors.Add(problem);
                    }
                }

                if (typeMap.ConstructorMap != null)
                    foreach (var p in typeMap.ConstructorMap.Parameters)
                        if (p.Source != null && !CanMap(p.Source.ValueType, p.ParameterType, new HashSet<TypePair>()))
                            errors.Add(typeMap + ", constructor parameter '" + p.Parameter.Name + "': cannot map " + p.Source.ValueType.FullName + " to " + p.ParameterType.FullName + ".");
            }
            if (errors.Count > 0) throw new MapperConfigurationException(errors);
        }

        private bool CanMap(Type source, Type destination, HashSet<TypePair> visited)
        {
            if (destination == typeof(object) || source == destination || source == typeof(object)) return true;
            if (!visited.Add(new TypePair(source, destination))) return true;
            if (_configuration.ResolveTypeMap(source, source, destination) != null) return true;

            var s = Nullable.GetUnderlyingType(source) ?? source;
            var d = Nullable.GetUnderlyingType(destination) ?? destination;
            if (d == typeof(string) || d.IsAssignableFrom(s)) return true;

            if (ReflectionHelper.IsCollectionType(d))
            {
                if (!ReflectionHelper.IsCollectionType(s)) return false;
                if (ReflectionHelper.TryGetDictionaryTypes(d, out var dk, out var dv) && ReflectionHelper.TryGetDictionaryTypes(s, out var sk, out var sv))
                    return CanMap(sk, dk, visited) && CanMap(sv, dv, visited);
                return CanMap(ReflectionHelper.GetElementType(s), ReflectionHelper.GetElementType(d), visited);
            }

            if (d.IsEnum) return s.IsEnum || s == typeof(string) || ReflectionHelper.IsNumeric(s);
            if (s.IsEnum && ReflectionHelper.IsNumeric(d)) return true;
            return Conversions.Get(s, d) != null;
        }
    }
}
