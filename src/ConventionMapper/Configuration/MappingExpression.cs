using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ConventionMapper.Internal;

namespace ConventionMapper.Configuration
{
    internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
    {
        private readonly TypeMapConfiguration _config;
        private readonly Profile? _profile;

        public MappingExpression(TypeMapConfiguration config, Profile? profile)
        {
            _config = config;
            _profile = profile;
        }

        private static TSource S(object? o) => CastHelper.Cast<TSource>(o);
        private static TDestination D(object? o) => CastHelper.Cast<TDestination>(o);

        public IMappingExpression<TSource, TDestination> ForMember<TMember>(Expression<Func<TDestination, TMember>> destinationMember, Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
        {
            if (destinationMember == null) throw new ArgumentNullException(nameof(destinationMember));
            if (memberOptions == null) throw new ArgumentNullException(nameof(memberOptions));
            var member = ExpressionHelper.GetTopLevelMember(destinationMember);
            memberOptions(new MemberConfigurationExpression<TSource, TDestination, TMember>(_config.GetOrAddMember(member.Name)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForMember(string name, Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions)
        {
            if (!ReflectionHelper.HasMember(typeof(TDestination), name))
                throw new ArgumentOutOfRangeException(nameof(name), typeof(TDestination).FullName + " has no member named '" + name + "'.");
            memberOptions(new MemberConfigurationExpression<TSource, TDestination, object>(_config.GetOrAddMember(name)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForPath<TMember>(Expression<Func<TDestination, TMember>> destinationMember, Action<IPathConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
        {
            var chain = ExpressionHelper.ExtractChain(destinationMember);
            if (chain == null || chain.Length == 0 || chain.Any(m => m is MethodInfo))
                throw new ArgumentException("ForPath requires a property/field chain such as d => d.Customer.Name.", nameof(destinationMember));

            var path = new PathConfig(chain);
            memberOptions(new PathConfigurationExpression<TSource, TDestination, TMember>(path));

            if (chain.Length == 1)
            {
                var member = _config.GetOrAddMember(chain[0].Name);
                member.Ignored = path.Ignored;
                if (path.Source != null) member.Source = path.Source;
                if (path.Condition != null)
                {
                    var condition = path.Condition;
                    member.Condition = (s, d, sm, dm, ctx) => condition(s, d);
                }
            }
            else
            {
                _config.Paths.RemoveAll(p => p.Path.SequenceEqual(chain));
                _config.Paths.Add(path);
            }
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForAllMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions)
        {
            foreach (var name in ReflectionHelper.GetDestinationMemberNames(typeof(TDestination)).Distinct())
                memberOptions(new MemberConfigurationExpression<TSource, TDestination, object>(_config.GetOrAddMember(name)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForSourceMember<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember, Action<ISourceMemberConfigurationExpression> memberOptions) =>
            ForSourceMember(ExpressionHelper.GetTopLevelMember(sourceMember).Name, memberOptions);

        public IMappingExpression<TSource, TDestination> ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> memberOptions)
        {
            memberOptions(new SourceMemberConfigurationExpression(_config, sourceMemberName));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<TSource>> paramOptions)
        {
            paramOptions(new CtorParamConfigurationExpression<TSource>(_config.GetOrAddCtorParam(ctorParamName)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ConstructUsing(Expression<Func<TSource, TDestination>> ctor)
        {
            var compiled = ctor.Compile();
            _config.CustomCtorExpression = ctor;
            _config.CustomCtor = (s, ctx) => compiled(S(s));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> ctor)
        {
            _config.CustomCtorExpression = null;
            _config.CustomCtor = (s, ctx) => ctor(S(s), ctx);
            return this;
        }

        public void ConvertUsing(Expression<Func<TSource, TDestination>> mappingExpression)
        {
            var compiled = mappingExpression.Compile();
            _config.TypeConverterExpression = mappingExpression;
            _config.TypeConverter = (s, d, ctx) => compiled(S(s));
        }

        public void ConvertUsing(Func<TSource, TDestination, TDestination> mappingFunction)
        {
            _config.TypeConverterExpression = null;
            _config.TypeConverter = (s, d, ctx) => mappingFunction(S(s), D(d));
        }

        public void ConvertUsing(Func<TSource, TDestination, ResolutionContext, TDestination> mappingFunction)
        {
            _config.TypeConverterExpression = null;
            _config.TypeConverter = (s, d, ctx) => mappingFunction(S(s), D(d), ctx);
        }

        public void ConvertUsing(ITypeConverter<TSource, TDestination> converter)
        {
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            _config.TypeConverterExpression = null;
            _config.TypeConverter = (s, d, ctx) => converter.Convert(S(s), D(d), ctx);
        }

        public void ConvertUsing<TTypeConverter>() where TTypeConverter : ITypeConverter<TSource, TDestination>
        {
            _config.TypeConverterExpression = null;
            _config.TypeConverter = (s, d, ctx) => ((TTypeConverter)ctx.CreateService(typeof(TTypeConverter))).Convert(S(s), D(d), ctx);
        }

        public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeFunction)
        {
            _config.BeforeMaps.Add((s, d, ctx) => beforeFunction(S(s), D(d)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination, ResolutionContext> beforeFunction)
        {
            _config.BeforeMaps.Add((s, d, ctx) => beforeFunction(S(s), D(d), ctx));
            return this;
        }

        public IMappingExpression<TSource, TDestination> BeforeMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>
        {
            _config.BeforeMaps.Add((s, d, ctx) => ((TMappingAction)ctx.CreateService(typeof(TMappingAction))).Process(S(s), D(d), ctx));
            return this;
        }

        public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction)
        {
            _config.AfterMaps.Add((s, d, ctx) => afterFunction(S(s), D(d)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> afterFunction)
        {
            _config.AfterMaps.Add((s, d, ctx) => afterFunction(S(s), D(d), ctx));
            return this;
        }

        public IMappingExpression<TSource, TDestination> AfterMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>
        {
            _config.AfterMaps.Add((s, d, ctx) => ((TMappingAction)ctx.CreateService(typeof(TMappingAction))).Process(S(s), D(d), ctx));
            return this;
        }

        public IMappingExpression<TSource, TDestination> Include<TOtherSource, TOtherDestination>() where TOtherSource : TSource where TOtherDestination : TDestination =>
            Include(typeof(TOtherSource), typeof(TOtherDestination));

        public IMappingExpression<TSource, TDestination> Include(Type derivedSourceType, Type derivedDestinationType)
        {
            _config.IncludedDerived.Add(new TypePair(derivedSourceType, derivedDestinationType));
            return this;
        }

        public IMappingExpression<TSource, TDestination> IncludeBase<TSourceBase, TDestinationBase>()
        {
            _config.IncludedBase.Add(new TypePair(typeof(TSourceBase), typeof(TDestinationBase)));
            return this;
        }

        public IMappingExpression<TSource, TDestination> IncludeMembers(params Expression<Func<TSource, object>>[] memberExpressions)
        {
            _config.IncludedMembers.AddRange(memberExpressions);
            return this;
        }

        public IMappingExpression<TDestination, TSource> ReverseMap()
        {
            if (_profile == null) throw new NotSupportedException("ReverseMap cannot be called from ForAllMaps.");
            var reverse = new TypeMapConfiguration(typeof(TDestination), typeof(TSource), MemberList.None) { ReverseOf = _config };
            _profile.TypeMapConfigs.Add(reverse);
            return new MappingExpression<TDestination, TSource>(reverse, _profile);
        }

        public IMappingExpression<TSource, TDestination> MaxDepth(int depth)
        {
            _config.MaxDepth = depth;
            return this;
        }

        public IMappingExpression<TSource, TDestination> PreserveReferences()
        {
            _config.PreserveReferences = true;
            return this;
        }

        public IMappingExpression<TSource, TDestination> DisableCtorValidation()
        {
            _config.DisableCtorValidation = true;
            return this;
        }

        public IMappingExpression<TSource, TDestination> AddTransform<TValue>(Expression<Func<TValue, TValue>> transformer)
        {
            _config.ValueTransformers.Add(ValueTransformerConfiguration.Create(transformer));
            return this;
        }

        internal TypeMapConfiguration Config => _config;

        public IMappingExpression<TSource, TDestination> EqualityComparison(Expression<Func<TSource, TDestination, bool>> comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));
            var compiled = comparison.Compile();
            _config.EqualityComparison = (s, d) => s is TSource typedSource && d is TDestination typedDestination && compiled(typedSource, typedDestination);
            return this;
        }

        public IMappingExpression<TSource, TDestination> IgnoreNullSourceValues()
        {
            _config.IgnoreNullSourceValues = true;
            return this;
        }

        public IMappingExpression<TSource, TDestination> IgnoreAllPropertiesWithAnInaccessibleSetter()
        {
            _config.IgnoreInaccessibleSetters = true;
            return this;
        }
    }

    internal sealed class MappingExpression : IMappingExpression
    {
        private readonly TypeMapConfiguration _config;
        private readonly Profile? _profile;

        public MappingExpression(TypeMapConfiguration config, Profile? profile)
        {
            _config = config;
            _profile = profile;
        }

        public IMappingExpression ForMember(string name, Action<IMemberConfigurationExpression> memberOptions)
        {
            memberOptions(new MemberConfigurationExpression(_config.GetOrAddMember(name)));
            return this;
        }

        public IMappingExpression ForAllMembers(Action<IMemberConfigurationExpression> memberOptions)
        {
            foreach (var name in ReflectionHelper.GetDestinationMemberNames(_config.DestinationType).Distinct())
                memberOptions(new MemberConfigurationExpression(_config.GetOrAddMember(name)));
            return this;
        }

        public IMappingExpression ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> memberOptions)
        {
            memberOptions(new SourceMemberConfigurationExpression(_config, sourceMemberName));
            return this;
        }

        public IMappingExpression ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<object>> paramOptions)
        {
            paramOptions(new CtorParamConfigurationExpression<object>(_config.GetOrAddCtorParam(ctorParamName)));
            return this;
        }

        public IMappingExpression ReverseMap()
        {
            if (_profile == null) throw new NotSupportedException("ReverseMap cannot be called from ForAllMaps.");
            var reverse = new TypeMapConfiguration(_config.DestinationType, _config.SourceType, MemberList.None) { ReverseOf = _config };
            _profile.TypeMapConfigs.Add(reverse);
            return new MappingExpression(reverse, _profile);
        }

        public IMappingExpression Include(Type derivedSourceType, Type derivedDestinationType)
        {
            _config.IncludedDerived.Add(new TypePair(derivedSourceType, derivedDestinationType));
            return this;
        }

        public IMappingExpression IncludeBase(Type sourceBase, Type destinationBase)
        {
            _config.IncludedBase.Add(new TypePair(sourceBase, destinationBase));
            return this;
        }

        public IMappingExpression ConvertUsing(Type typeConverterType)
        {
            if (typeConverterType == null) throw new ArgumentNullException(nameof(typeConverterType));
            _config.TypeConverterType = typeConverterType;
            _config.TypeConverterExpression = null;
            _config.TypeConverter = typeConverterType.IsGenericTypeDefinition ? null : TypeMapConfiguration.CreateReflectionConverter(typeConverterType);
            return this;
        }

        public IMappingExpression ConstructUsing(Func<object, ResolutionContext, object> ctor)
        {
            _config.CustomCtor = ctor;
            _config.CustomCtorExpression = null;
            return this;
        }

        public IMappingExpression BeforeMap(Action<object, object> beforeFunction)
        {
            _config.BeforeMaps.Add((s, d, ctx) => beforeFunction(s, d));
            return this;
        }

        public IMappingExpression AfterMap(Action<object, object> afterFunction)
        {
            _config.AfterMaps.Add((s, d, ctx) => afterFunction(s, d));
            return this;
        }

        public IMappingExpression MaxDepth(int depth)
        {
            _config.MaxDepth = depth;
            return this;
        }

        public IMappingExpression PreserveReferences()
        {
            _config.PreserveReferences = true;
            return this;
        }

        public IMappingExpression DisableCtorValidation()
        {
            _config.DisableCtorValidation = true;
            return this;
        }

        public IMappingExpression IgnoreNullSourceValues()
        {
            _config.IgnoreNullSourceValues = true;
            return this;
        }

        public IMappingExpression IgnoreAllPropertiesWithAnInaccessibleSetter()
        {
            _config.IgnoreInaccessibleSetters = true;
            return this;
        }
    }
}
