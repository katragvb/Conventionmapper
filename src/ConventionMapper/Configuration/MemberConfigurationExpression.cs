using System;
using System.Linq.Expressions;
using ConventionMapper.Internal;

namespace ConventionMapper.Configuration
{
    internal class MemberConfigurationExpression<TSource, TDestination, TMember> : IMemberConfigurationExpression<TSource, TDestination, TMember>
    {
        private readonly MemberConfig _config;

        public MemberConfigurationExpression(MemberConfig config) { _config = config; }

        private static TSource S(object? o) => CastHelper.Cast<TSource>(o);
        private static TDestination D(object? o) => CastHelper.Cast<TDestination>(o);
        private static TMember M(object? o) => CastHelper.Cast<TMember>(o);

        private void SetSource(ValueSource source)
        {
            _config.Ignored = false;
            _config.Source = source;
        }

        // ----- MapFrom -----

        public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> mapExpression) => SetSource(ValueSources.FromExpression(mapExpression));

        public void MapFrom<TResult>(Func<TSource, TDestination, TResult> mappingFunction) =>
            SetSource(new DelegateSource(typeof(TResult), (s, d, m, ctx) => mappingFunction(S(s), D(d))));

        public void MapFrom<TResult>(Func<TSource, TDestination, TMember, ResolutionContext, TResult> mappingFunction) =>
            SetSource(new DelegateSource(typeof(TResult), (s, d, m, ctx) => mappingFunction(S(s), D(d), M(m), ctx)));

        public void MapFrom<TValueResolver>() where TValueResolver : IValueResolver<TSource, TDestination, TMember> =>
            SetSource(new DelegateSource(typeof(TMember), (s, d, m, ctx) =>
                ((TValueResolver)ctx.CreateService(typeof(TValueResolver))).Resolve(S(s), D(d), M(m), ctx)));

        public void MapFrom(IValueResolver<TSource, TDestination, TMember> valueResolver)
        {
            if (valueResolver == null) throw new ArgumentNullException(nameof(valueResolver));
            SetSource(new DelegateSource(typeof(TMember), (s, d, m, ctx) => valueResolver.Resolve(S(s), D(d), M(m), ctx)));
        }

        public void MapFrom<TValueResolver, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
            where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>
        {
            var inner = ValueSources.FromExpression(sourceMember);
            SetSource(new DelegateSource(typeof(TMember), (s, d, m, ctx) =>
                ((TValueResolver)ctx.CreateService(typeof(TValueResolver)))
                    .Resolve(S(s), D(d), CastHelper.Cast<TSourceMember>(inner.Resolve(s, d, m, ctx)), M(m), ctx)));
        }

        public void MapFrom(string sourceMembersPath) => SetSource(new PathStringSource(sourceMembersPath, false));

        // ----- Value converters -----

        private void SetConverter<TSourceMember>(ValueSource inner, Func<ResolutionContext, IValueConverter<TSourceMember, TMember>> converter) =>
            SetSource(new ConverterSource(inner, typeof(TMember), (value, ctx) => converter(ctx).Convert(CastHelper.Cast<TSourceMember>(value), ctx)));

        private static Func<ResolutionContext, IValueConverter<TSourceMember, TMember>> FromServices<TValueConverter, TSourceMember>()
            where TValueConverter : IValueConverter<TSourceMember, TMember> =>
            ctx => (TValueConverter)ctx.CreateService(typeof(TValueConverter));

        public void ConvertUsing<TValueConverter, TSourceMember>() where TValueConverter : IValueConverter<TSourceMember, TMember> =>
            SetConverter<TSourceMember>(new PathStringSource(_config.Name, true), FromServices<TValueConverter, TSourceMember>());

        public void ConvertUsing<TValueConverter, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember) where TValueConverter : IValueConverter<TSourceMember, TMember> =>
            SetConverter<TSourceMember>(ValueSources.FromExpression(sourceMember), FromServices<TValueConverter, TSourceMember>());

        public void ConvertUsing<TValueConverter, TSourceMember>(string sourceMemberName) where TValueConverter : IValueConverter<TSourceMember, TMember> =>
            SetConverter<TSourceMember>(new PathStringSource(sourceMemberName, false), FromServices<TValueConverter, TSourceMember>());

        public void ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> valueConverter) =>
            SetConverter<TSourceMember>(new PathStringSource(_config.Name, true), _ => valueConverter);

        public void ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> valueConverter, Expression<Func<TSource, TSourceMember>> sourceMember) =>
            SetConverter<TSourceMember>(ValueSources.FromExpression(sourceMember), _ => valueConverter);

        // ----- Misc options -----

        public void Ignore()
        {
            _config.Ignored = true;
            _config.Source = null;
        }

        public void UseDestinationValue() => _config.UseDestinationValue = true;
        public void DoNotUseDestinationValue() => _config.UseDestinationValue = false;

        public void NullSubstitute(object? nullSubstitute)
        {
            _config.HasNullSubstitute = true;
            _config.NullSubstitute = nullSubstitute;
        }

        public void SetMappingOrder(int mappingOrder) => _config.MappingOrder = mappingOrder;

        public void ExplicitExpansion() => _config.ExplicitExpansion = true;

        public void AddTransform(Expression<Func<TMember, TMember>> transformer) => _config.Transformers.Add(ValueTransformerConfiguration.Create(transformer));

        // ----- Conditions -----

        public void Condition(Func<TSource, bool> condition) => _config.Condition = (s, d, sm, dm, ctx) => condition(S(s));
        public void Condition(Func<TSource, TDestination, bool> condition) => _config.Condition = (s, d, sm, dm, ctx) => condition(S(s), D(d));
        public void Condition(Func<TSource, TDestination, TMember, bool> condition) => _config.Condition = (s, d, sm, dm, ctx) => condition(S(s), D(d), M(sm));
        public void Condition(Func<TSource, TDestination, TMember, TMember, bool> condition) => _config.Condition = (s, d, sm, dm, ctx) => condition(S(s), D(d), M(sm), M(dm));
        public void Condition(Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool> condition) => _config.Condition = (s, d, sm, dm, ctx) => condition(S(s), D(d), M(sm), M(dm), ctx);

        public void Condition<TCondition>() where TCondition : ICondition<TSource, TDestination, TMember> =>
            _config.Condition = (s, d, sm, dm, ctx) => ((TCondition)ctx.CreateService(typeof(TCondition))).Evaluate(S(s), D(d), M(sm), M(dm), ctx);

        public void PreCondition(Func<TSource, bool> condition) => _config.PreCondition = (s, d, ctx) => condition(S(s));
        public void PreCondition(Func<TSource, ResolutionContext, bool> condition) => _config.PreCondition = (s, d, ctx) => condition(S(s), ctx);
        public void PreCondition(Func<TSource, TDestination, ResolutionContext, bool> condition) => _config.PreCondition = (s, d, ctx) => condition(S(s), D(d), ctx);

        public void PreCondition<TPreCondition>() where TPreCondition : IPreCondition<TSource, TDestination> =>
            _config.PreCondition = (s, d, ctx) => ((TPreCondition)ctx.CreateService(typeof(TPreCondition))).Evaluate(S(s), D(d), ctx);
    }

    internal sealed class MemberConfigurationExpression : MemberConfigurationExpression<object, object, object>, IMemberConfigurationExpression
    {
        public MemberConfigurationExpression(MemberConfig config) : base(config) { }
    }

    internal sealed class CtorParamConfigurationExpression<TSource> : ICtorParamConfigurationExpression<TSource>
    {
        private readonly CtorParamConfig _config;

        public CtorParamConfigurationExpression(CtorParamConfig config) { _config = config; }

        public void MapFrom<TMember>(Expression<Func<TSource, TMember>> sourceMember) => _config.Source = ValueSources.FromExpression(sourceMember);

        public void MapFrom<TMember>(Func<TSource, ResolutionContext, TMember> resolver) =>
            _config.Source = new DelegateSource(typeof(TMember), (s, d, m, ctx) => resolver(CastHelper.Cast<TSource>(s), ctx));

        public void MapFrom(string sourceMembersPath) => _config.Source = new PathStringSource(sourceMembersPath, false);
    }

    internal sealed class PathConfigurationExpression<TSource, TDestination, TMember> : IPathConfigurationExpression<TSource, TDestination, TMember>
    {
        private readonly PathConfig _config;

        public PathConfigurationExpression(PathConfig config) { _config = config; }

        public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        {
            _config.Ignored = false;
            _config.Source = ValueSources.FromExpression(sourceMember);
        }

        public void Ignore()
        {
            _config.Ignored = true;
            _config.Source = null;
        }

        public void Condition(Func<TSource, TDestination, bool> condition) =>
            _config.Condition = (s, d) => condition(CastHelper.Cast<TSource>(s), CastHelper.Cast<TDestination>(d));
    }

    internal sealed class SourceMemberConfigurationExpression : ISourceMemberConfigurationExpression
    {
        private readonly TypeMapConfiguration _config;
        private readonly string _name;

        public SourceMemberConfigurationExpression(TypeMapConfiguration config, string name)
        {
            _config = config;
            _name = name;
        }

        public void DoNotValidate() => _config.SourceMembersNotValidated.Add(_name);
    }
}
