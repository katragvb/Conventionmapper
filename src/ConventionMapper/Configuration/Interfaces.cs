using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace ConventionMapper
{
    /// <summary>Settings and map creation available on profiles and on the root configuration.</summary>
    public interface IProfileExpression
    {
        string ProfileName { get; }
        bool AllowNullCollections { get; set; }
        bool AllowNullDestinationValues { get; set; }
        Func<PropertyInfo, bool> ShouldMapProperty { get; set; }
        Func<FieldInfo, bool> ShouldMapField { get; set; }
        Func<MethodInfo, bool> ShouldMapMethod { get; set; }
        INamingConvention SourceMemberNamingConvention { get; set; }
        INamingConvention DestinationMemberNamingConvention { get; set; }
        IList<ValueTransformerConfiguration> ValueTransformers { get; }
        /// <summary>When true, destination members are left untouched when their source value is null.</summary>
        bool IgnoreNullSourceValues { get; set; }

        IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
        IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList);
        IMappingExpression CreateMap(Type sourceType, Type destinationType);
        IMappingExpression CreateMap(Type sourceType, Type destinationType, MemberList memberList);

        void RecognizePrefixes(params string[] prefixes);
        void RecognizePostfixes(params string[] postfixes);
        void RecognizeDestinationPrefixes(params string[] prefixes);
        void RecognizeDestinationPostfixes(params string[] postfixes);
        void ClearPrefixes();
        void ReplaceMemberName(string original, string newValue);
        void AddGlobalIgnore(string propertyNameStartingWith);
        void ForAllMaps(Action<TypeMap, IMappingExpression> configuration);
        void DisableConstructorMapping();
    }

    /// <summary>Root configuration (the <c>cfg</c> in <c>new MapperConfiguration(cfg =&gt; ...)</c>).</summary>
    public interface IMapperConfigurationExpression : IProfileExpression
    {
        void AddProfile(Profile profile);
        void AddProfile<TProfile>() where TProfile : Profile, new();
        void AddProfile(Type profileType);
        void AddProfiles(IEnumerable<Profile> profiles);
        void AddMaps(IEnumerable<Assembly> assembliesToScan);
        void AddMaps(params Assembly[] assembliesToScan);
        void AddMaps(params Type[] typesFromAssembliesContainingMappingDefinitions);
        void ConstructServicesUsing(Func<Type, object> constructor);

        /// <summary>Compile maps into delegates on first use (default true). Set false to always use the interpreter.</summary>
        bool UseCompiledMappings { get; set; }

        /// <summary>AssertConfigurationIsValid also reports members whose source may be null while the destination is not nullable.</summary>
        bool ValidateNullability { get; set; }
    }

    /// <summary>Strongly typed map configuration.</summary>
    public interface IMappingExpression<TSource, TDestination>
    {
        IMappingExpression<TSource, TDestination> ForMember<TMember>(Expression<Func<TDestination, TMember>> destinationMember, Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);
        IMappingExpression<TSource, TDestination> ForMember(string name, Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions);
        IMappingExpression<TSource, TDestination> ForPath<TMember>(Expression<Func<TDestination, TMember>> destinationMember, Action<IPathConfigurationExpression<TSource, TDestination, TMember>> memberOptions);
        IMappingExpression<TSource, TDestination> ForAllMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions);
        IMappingExpression<TSource, TDestination> ForSourceMember<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember, Action<ISourceMemberConfigurationExpression> memberOptions);
        IMappingExpression<TSource, TDestination> ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> memberOptions);
        IMappingExpression<TSource, TDestination> ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<TSource>> paramOptions);

        IMappingExpression<TSource, TDestination> ConstructUsing(Expression<Func<TSource, TDestination>> ctor);
        IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> ctor);

        void ConvertUsing(Expression<Func<TSource, TDestination>> mappingExpression);
        void ConvertUsing(Func<TSource, TDestination, TDestination> mappingFunction);
        void ConvertUsing(Func<TSource, TDestination, ResolutionContext, TDestination> mappingFunction);
        void ConvertUsing(ITypeConverter<TSource, TDestination> converter);
        void ConvertUsing<TTypeConverter>() where TTypeConverter : ITypeConverter<TSource, TDestination>;

        IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeFunction);
        IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination, ResolutionContext> beforeFunction);
        IMappingExpression<TSource, TDestination> BeforeMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>;
        IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction);
        IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> afterFunction);
        IMappingExpression<TSource, TDestination> AfterMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>;

        IMappingExpression<TSource, TDestination> Include<TOtherSource, TOtherDestination>() where TOtherSource : TSource where TOtherDestination : TDestination;
        IMappingExpression<TSource, TDestination> Include(Type derivedSourceType, Type derivedDestinationType);
        IMappingExpression<TSource, TDestination> IncludeBase<TSourceBase, TDestinationBase>();
        IMappingExpression<TSource, TDestination> IncludeMembers(params Expression<Func<TSource, object>>[] memberExpressions);

        IMappingExpression<TDestination, TSource> ReverseMap();

        IMappingExpression<TSource, TDestination> MaxDepth(int depth);
        IMappingExpression<TSource, TDestination> PreserveReferences();
        IMappingExpression<TSource, TDestination> DisableCtorValidation();
        IMappingExpression<TSource, TDestination> AddTransform<TValue>(Expression<Func<TValue, TValue>> transformer);
        IMappingExpression<TSource, TDestination> IgnoreAllPropertiesWithAnInaccessibleSetter();

        /// <summary>
        /// Match collection items by key when mapping into an existing collection: matched items are updated in place,
        /// new items are added and items missing from the source are removed (ideal for EF Core child collections).
        /// </summary>
        IMappingExpression<TSource, TDestination> EqualityComparison(Expression<Func<TSource, TDestination, bool>> comparison);

        /// <summary>Leave destination members untouched when their source value is null (PATCH semantics).</summary>
        IMappingExpression<TSource, TDestination> IgnoreNullSourceValues();
    }

    /// <summary>Untyped map configuration (CreateMap(Type, Type), open generics, ForAllMaps).</summary>
    public interface IMappingExpression
    {
        IMappingExpression ForMember(string name, Action<IMemberConfigurationExpression> memberOptions);
        IMappingExpression ForAllMembers(Action<IMemberConfigurationExpression> memberOptions);
        IMappingExpression ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> memberOptions);
        IMappingExpression ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<object>> paramOptions);
        IMappingExpression ReverseMap();
        IMappingExpression Include(Type derivedSourceType, Type derivedDestinationType);
        IMappingExpression IncludeBase(Type sourceBase, Type destinationBase);
        IMappingExpression ConvertUsing(Type typeConverterType);
        IMappingExpression ConstructUsing(Func<object, ResolutionContext, object> ctor);
        IMappingExpression BeforeMap(Action<object, object> beforeFunction);
        IMappingExpression AfterMap(Action<object, object> afterFunction);
        IMappingExpression MaxDepth(int depth);
        IMappingExpression PreserveReferences();
        IMappingExpression DisableCtorValidation();
        IMappingExpression IgnoreAllPropertiesWithAnInaccessibleSetter();
        IMappingExpression IgnoreNullSourceValues();
    }

    /// <summary>Destination member options (<c>opt =&gt; opt.MapFrom(...)</c>).</summary>
    public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
    {
        void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> mapExpression);
        void MapFrom<TResult>(Func<TSource, TDestination, TResult> mappingFunction);
        void MapFrom<TResult>(Func<TSource, TDestination, TMember, ResolutionContext, TResult> mappingFunction);
        void MapFrom<TValueResolver>() where TValueResolver : IValueResolver<TSource, TDestination, TMember>;
        void MapFrom(IValueResolver<TSource, TDestination, TMember> valueResolver);
        void MapFrom<TValueResolver, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember) where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>;
        void MapFrom(string sourceMembersPath);

        void ConvertUsing<TValueConverter, TSourceMember>() where TValueConverter : IValueConverter<TSourceMember, TMember>;
        void ConvertUsing<TValueConverter, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember) where TValueConverter : IValueConverter<TSourceMember, TMember>;
        void ConvertUsing<TValueConverter, TSourceMember>(string sourceMemberName) where TValueConverter : IValueConverter<TSourceMember, TMember>;
        void ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> valueConverter);
        void ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> valueConverter, Expression<Func<TSource, TSourceMember>> sourceMember);

        void Ignore();
        void UseDestinationValue();
        void DoNotUseDestinationValue();
        void NullSubstitute(object? nullSubstitute);
        void SetMappingOrder(int mappingOrder);
        void AddTransform(Expression<Func<TMember, TMember>> transformer);

        /// <summary>ProjectTo only includes this member when it is requested in membersToExpand. Map() always maps it.</summary>
        void ExplicitExpansion();

        void Condition(Func<TSource, bool> condition);
        void Condition(Func<TSource, TDestination, bool> condition);
        void Condition(Func<TSource, TDestination, TMember, bool> condition);
        void Condition(Func<TSource, TDestination, TMember, TMember, bool> condition);
        void Condition(Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool> condition);
        void Condition<TCondition>() where TCondition : ICondition<TSource, TDestination, TMember>;

        void PreCondition(Func<TSource, bool> condition);
        void PreCondition(Func<TSource, ResolutionContext, bool> condition);
        void PreCondition(Func<TSource, TDestination, ResolutionContext, bool> condition);
        void PreCondition<TPreCondition>() where TPreCondition : IPreCondition<TSource, TDestination>;
    }

    /// <summary>Untyped member options.</summary>
    public interface IMemberConfigurationExpression : IMemberConfigurationExpression<object, object, object> { }

    /// <summary>Constructor parameter options.</summary>
    public interface ICtorParamConfigurationExpression<TSource>
    {
        void MapFrom<TMember>(Expression<Func<TSource, TMember>> sourceMember);
        void MapFrom<TMember>(Func<TSource, ResolutionContext, TMember> resolver);
        void MapFrom(string sourceMembersPath);
    }

    /// <summary>Deep destination path options (<c>ForPath(d =&gt; d.Customer.Name, ...)</c>).</summary>
    public interface IPathConfigurationExpression<TSource, TDestination, TMember>
    {
        void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);
        void Ignore();
        void Condition(Func<TSource, TDestination, bool> condition);
    }

    /// <summary>Source member options.</summary>
    public interface ISourceMemberConfigurationExpression
    {
        void DoNotValidate();
    }
}
