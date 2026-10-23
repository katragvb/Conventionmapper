using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConventionMapper.Configuration;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>Groups related maps and settings. Derive from it and call CreateMap in the constructor.</summary>
    public abstract class Profile : IProfileExpression
    {
        internal readonly List<TypeMapConfiguration> TypeMapConfigs = new List<TypeMapConfiguration>();
        internal readonly List<Action<TypeMap, IMappingExpression>> AllMapsActions = new List<Action<TypeMap, IMappingExpression>>();
        internal readonly List<string> PrefixList = new List<string> { "Get" };
        internal readonly List<string> PostfixList = new List<string>();
        internal readonly List<string> DestinationPrefixList = new List<string>();
        internal readonly List<string> DestinationPostfixList = new List<string>();
        internal readonly List<KeyValuePair<string, string>> MemberNameReplacers = new List<KeyValuePair<string, string>>();
        internal readonly List<string> GlobalIgnores = new List<string>();
        internal bool PrefixesCleared;
        internal bool? AllowNullCollectionsValue;
        internal bool? AllowNullDestinationValuesValue;
        internal bool? DisableConstructorMappingValue;
        internal bool? IgnoreNullSourceValuesValue;
        internal Func<PropertyInfo, bool>? ShouldMapPropertyValue;
        internal Func<FieldInfo, bool>? ShouldMapFieldValue;
        internal Func<MethodInfo, bool>? ShouldMapMethodValue;
        internal INamingConvention? SourceConventionValue;
        internal INamingConvention? DestinationConventionValue;
        private readonly List<ValueTransformerConfiguration> _valueTransformers = new List<ValueTransformerConfiguration>();

        protected Profile() : this(null) { }

        protected Profile(string? profileName)
        {
            ProfileName = profileName ?? GetType().FullName ?? GetType().Name;
        }

        protected Profile(string profileName, Action<IProfileExpression> configurationAction) : this(profileName)
        {
            if (configurationAction == null) throw new ArgumentNullException(nameof(configurationAction));
            configurationAction(this);
        }

        public string ProfileName { get; }

        public bool AllowNullCollections { get => AllowNullCollectionsValue ?? false; set => AllowNullCollectionsValue = value; }
        public bool AllowNullDestinationValues { get => AllowNullDestinationValuesValue ?? true; set => AllowNullDestinationValuesValue = value; }
        public bool IgnoreNullSourceValues { get => IgnoreNullSourceValuesValue ?? false; set => IgnoreNullSourceValuesValue = value; }
        public Func<PropertyInfo, bool> ShouldMapProperty { get => ShouldMapPropertyValue ?? (_ => true); set => ShouldMapPropertyValue = value; }
        public Func<FieldInfo, bool> ShouldMapField { get => ShouldMapFieldValue ?? (f => f.IsPublic); set => ShouldMapFieldValue = value; }
        public Func<MethodInfo, bool> ShouldMapMethod { get => ShouldMapMethodValue ?? (_ => true); set => ShouldMapMethodValue = value; }
        public INamingConvention SourceMemberNamingConvention { get => SourceConventionValue ?? PascalCaseNamingConvention.Instance; set => SourceConventionValue = value; }
        public INamingConvention DestinationMemberNamingConvention { get => DestinationConventionValue ?? PascalCaseNamingConvention.Instance; set => DestinationConventionValue = value; }
        public IList<ValueTransformerConfiguration> ValueTransformers => _valueTransformers;

        public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>() => CreateMap<TSource, TDestination>(MemberList.Destination);

        public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList)
        {
            var config = new TypeMapConfiguration(typeof(TSource), typeof(TDestination), memberList);
            TypeMapConfigs.Add(config);
            return new MappingExpression<TSource, TDestination>(config, this);
        }

        public IMappingExpression CreateMap(Type sourceType, Type destinationType) => CreateMap(sourceType, destinationType, MemberList.Destination);

        public IMappingExpression CreateMap(Type sourceType, Type destinationType, MemberList memberList)
        {
            if (sourceType.IsGenericTypeDefinition != destinationType.IsGenericTypeDefinition && destinationType.IsGenericTypeDefinition)
                throw new ArgumentException("An open generic destination requires an open generic source.", nameof(sourceType));
            var config = new TypeMapConfiguration(sourceType, destinationType, memberList);
            TypeMapConfigs.Add(config);
            return new MappingExpression(config, this);
        }

        public void RecognizePrefixes(params string[] prefixes) => PrefixList.AddRange(prefixes);
        public void RecognizePostfixes(params string[] postfixes) => PostfixList.AddRange(postfixes);
        public void RecognizeDestinationPrefixes(params string[] prefixes) => DestinationPrefixList.AddRange(prefixes);
        public void RecognizeDestinationPostfixes(params string[] postfixes) => DestinationPostfixList.AddRange(postfixes);

        public void ClearPrefixes()
        {
            PrefixList.Clear();
            PrefixesCleared = true;
        }

        public void ReplaceMemberName(string original, string newValue) => MemberNameReplacers.Add(new KeyValuePair<string, string>(original, newValue));
        public void AddGlobalIgnore(string propertyNameStartingWith) => GlobalIgnores.Add(propertyNameStartingWith);
        public void ForAllMaps(Action<TypeMap, IMappingExpression> configuration) => AllMapsActions.Add(configuration ?? throw new ArgumentNullException(nameof(configuration)));
        public void DisableConstructorMapping() => DisableConstructorMappingValue = true;
    }

    /// <summary>The root configuration object passed to <c>new MapperConfiguration(cfg =&gt; ...)</c>.</summary>
    public sealed class MapperConfigurationExpression : Profile, IMapperConfigurationExpression
    {
        internal readonly List<Profile> Profiles = new List<Profile>();
        private readonly HashSet<Type> _profileTypes = new HashSet<Type>();
        private readonly HashSet<Assembly> _scannedAssemblies = new HashSet<Assembly>();
        internal Func<Type, object>? ServiceCtorValue;

        public MapperConfigurationExpression() : base("Default") { }

        public void AddProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile is MapperConfigurationExpression) throw new ArgumentException("Cannot add a MapperConfigurationExpression as a profile.", nameof(profile));
            if (!(profile is AttributeProfile) && !_profileTypes.Add(profile.GetType())) return;
            Profiles.Add(profile);
        }

        public void AddProfile<TProfile>() where TProfile : Profile, new()
        {
            if (_profileTypes.Contains(typeof(TProfile))) return;
            AddProfile(new TProfile());
        }

        public void AddProfile(Type profileType)
        {
            if (profileType == null) throw new ArgumentNullException(nameof(profileType));
            if (!typeof(Profile).IsAssignableFrom(profileType)) throw new ArgumentException(profileType + " does not derive from Profile.", nameof(profileType));
            if (_profileTypes.Contains(profileType)) return;
            AddProfile((Profile)Activator.CreateInstance(profileType, true)!);
        }

        public void AddProfiles(IEnumerable<Profile> profiles)
        {
            foreach (var profile in profiles) AddProfile(profile);
        }

        public void AddMaps(params Assembly[] assembliesToScan) => AddMaps((IEnumerable<Assembly>)assembliesToScan);

        public void AddMaps(params Type[] typesFromAssembliesContainingMappingDefinitions) =>
            AddMaps(typesFromAssembliesContainingMappingDefinitions.Select(t => t.Assembly));

        public void AddMaps(IEnumerable<Assembly> assembliesToScan)
        {
            if (assembliesToScan == null) throw new ArgumentNullException(nameof(assembliesToScan));
            foreach (var assembly in assembliesToScan.Where(a => a != null).Distinct())
            {
                if (!_scannedAssemblies.Add(assembly)) continue;
                var types = ReflectionHelper.GetLoadableTypes(assembly).ToList();

                foreach (var type in types)
                {
                    if (!typeof(Profile).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters) continue;
                    if (type == typeof(MapperConfigurationExpression) || type == typeof(AttributeProfile)) continue;
                    if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null) continue;
                    AddProfile(type);
                }

                AttributeProfile? attributeProfile = null;
                foreach (var type in types)
                {
                    foreach (var attribute in type.GetCustomAttributes<AutoMapAttribute>(false))
                    {
                        attributeProfile ??= new AttributeProfile("AttributeMaps:" + assembly.GetName().Name);
                        var map = attributeProfile.CreateMap(attribute.SourceType, type, attribute.MemberList);
                        if (attribute.MaxDepth > 0) map.MaxDepth(attribute.MaxDepth);
                        if (attribute.PreserveReferences) map.PreserveReferences();
                        if (attribute.ReverseMap) map.ReverseMap();
                    }
                }
                if (attributeProfile != null) AddProfile(attributeProfile);
            }
        }

        public void ConstructServicesUsing(Func<Type, object> constructor) => ServiceCtorValue = constructor;
    }

    internal sealed class AttributeProfile : Profile
    {
        public AttributeProfile(string name) : base(name) { }
    }
}
