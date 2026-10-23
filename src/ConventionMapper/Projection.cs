using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    public static class QueryableExtensions
    {
        /// <summary>
        /// <c>db.Orders.ProjectTo&lt;OrderDto&gt;(config)</c> — builds a Select expression from the configured maps so
        /// only the needed columns are queried. Only expression-based configuration is translatable.
        /// </summary>
        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IConfigurationProvider configuration)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var lambda = configuration.BuildProjection(source.ElementType, typeof(TDestination));
            var call = Expression.Call(typeof(Queryable), nameof(Queryable.Select), new[] { source.ElementType, typeof(TDestination) }, source.Expression, Expression.Quote(lambda));
            return source.Provider.CreateQuery<TDestination>(call);
        }

        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IMapper mapper) =>
            source.ProjectTo<TDestination>((mapper ?? throw new ArgumentNullException(nameof(mapper))).ConfigurationProvider);
    }

    internal sealed class ProjectionBuilder
    {
        private readonly MapperConfiguration _configuration;

        public ProjectionBuilder(MapperConfiguration configuration) { _configuration = configuration; }

        public LambdaExpression Build(Type sourceType, Type destinationType)
        {
            var parameter = Expression.Parameter(sourceType, "src");
            var body = BuildValue(parameter, destinationType, new Dictionary<TypeMap, int>(), true)
                       ?? throw new MappingException("Unable to build a projection from " + sourceType.FullName + " to " + destinationType.FullName + ".");
            if (body.Type != destinationType) body = Expression.Convert(body, destinationType);
            return Expression.Lambda(typeof(Func<,>).MakeGenericType(sourceType, destinationType), body, parameter);
        }

        private Expression? BuildValue(Expression source, Type destinationType, Dictionary<TypeMap, int> depth, bool topLevel = false)
        {
            var typeMap = _configuration.ResolveTypeMap(source.Type, source.Type, destinationType);
            if (typeMap != null)
            {
                var body = BuildTypeMap(typeMap, source, depth);
                if (body == null) return null;
                if (body.Type != destinationType) body = Expression.Convert(body, destinationType);
                if (!topLevel && (!source.Type.IsValueType || Nullable.GetUnderlyingType(source.Type) != null))
                    return Expression.Condition(Expression.Equal(source, Expression.Constant(null, source.Type)), Expression.Default(destinationType), body);
                return body;
            }

            if (destinationType == source.Type) return source;

            if (destinationType != typeof(string) && source.Type != typeof(string) &&
                ReflectionHelper.IsCollectionType(destinationType) && ReflectionHelper.IsCollectionType(source.Type))
                return BuildCollection(source, destinationType, depth);

            if (destinationType.IsAssignableFrom(source.Type)) return Expression.Convert(source, destinationType);

            if (destinationType == typeof(string))
                return Expression.Call(source, source.Type.GetMethod(nameof(ToString), Type.EmptyTypes) ?? typeof(object).GetMethod(nameof(ToString))!);

            try
            {
                return Expression.Convert(source, destinationType);
            }
            catch (InvalidOperationException)
            {
                throw new MappingException("Unable to project " + source.Type.FullName + " to " + destinationType.FullName +
                                           ": no type map and no built-in conversion. Create a map, or use MapFrom with a translatable expression.");
            }
        }

        private Expression? BuildTypeMap(TypeMap typeMap, Expression source, Dictionary<TypeMap, int> depth)
        {
            if (typeMap.TypeConverterExpression != null) return ExpressionHelper.ReplaceParameter(typeMap.TypeConverterExpression, source);
            if (typeMap.TypeConverter != null)
                throw new MappingException(typeMap + ": ConvertUsing with a delegate or ITypeConverter cannot be used with ProjectTo. Use ConvertUsing(src => ...) with an expression.");

            depth.TryGetValue(typeMap, out var current);
            var limit = typeMap.MaxDepth > 0 ? typeMap.MaxDepth : 1;
            if (current >= limit) return null;
            depth[typeMap] = current + 1;
            try
            {
                NewExpression newExpression;
                var bindings = new List<MemberBinding>();
                var bound = new HashSet<string>(StringComparer.Ordinal);

                if (typeMap.CustomCtorExpression != null)
                {
                    var ctorBody = ExpressionHelper.StripConvert(ExpressionHelper.ReplaceParameter(typeMap.CustomCtorExpression, source));
                    if (ctorBody is NewExpression ne)
                    {
                        newExpression = ne;
                    }
                    else if (ctorBody is MemberInitExpression mi)
                    {
                        newExpression = mi.NewExpression;
                        foreach (var binding in mi.Bindings)
                        {
                            bindings.Add(binding);
                            bound.Add(binding.Member.Name);
                        }
                    }
                    else throw new MappingException(typeMap + ": ConstructUsing must be a 'new' expression to be used with ProjectTo.");
                }
                else if (typeMap.CustomCtor != null)
                {
                    throw new MappingException(typeMap + ": ConstructUsing with a delegate cannot be used with ProjectTo. Use ConstructUsing(src => new ...).");
                }
                else if (typeMap.ConstructorMap != null)
                {
                    var args = new List<Expression>();
                    foreach (var p in typeMap.ConstructorMap.Parameters)
                    {
                        if (p.Source == null)
                        {
                            args.Add(p.DefaultValue == null ? (Expression)Expression.Default(p.ParameterType) : Expression.Convert(Expression.Constant(p.DefaultValue), p.ParameterType));
                            continue;
                        }
                        var sourceExpression = p.Source.BuildProjection(source) ?? throw NotProjectable(typeMap, "constructor parameter " + p.Parameter.Name);
                        var arg = BuildValue(sourceExpression, p.ParameterType, depth) ?? Expression.Default(p.ParameterType);
                        args.Add(arg.Type == p.ParameterType ? arg : Expression.Convert(arg, p.ParameterType));
                    }
                    newExpression = Expression.New(typeMap.ConstructorMap.Constructor, args);
                }
                else
                {
                    if (!typeMap.DestinationType.IsValueType && typeMap.DestinationType.GetConstructor(Type.EmptyTypes) == null)
                        throw new MappingException(typeMap + ": ProjectTo requires a public parameterless or resolvable constructor on " + typeMap.DestinationType.FullName + ".");
                    newExpression = Expression.New(typeMap.DestinationType);
                }

                foreach (var map in typeMap.OrderedPropertyMaps)
                {
                    if (bound.Contains(map.DestinationName) || !map.DestinationMember.CanWrite) continue;
                    var member = map.DestinationMember.Member;
                    if (!(member is PropertyInfo) && !(member is FieldInfo)) continue;

                    var sourceExpression = map.Source!.BuildProjection(source) ?? throw NotProjectable(typeMap, map.DestinationName);
                    var value = BuildValue(sourceExpression, map.DestinationType, depth);
                    if (value == null) continue;
                    if (value.Type != map.DestinationType) value = Expression.Convert(value, map.DestinationType);
                    if (map.HasNullSubstitute && map.NullSubstitute != null && (!value.Type.IsValueType || Nullable.GetUnderlyingType(value.Type) != null))
                    {
                        try
                        {
                            value = Expression.Coalesce(value, Expression.Convert(Expression.Constant(map.NullSubstitute), value.Type));
                        }
                        catch (Exception)
                        {
                            // leave the value as-is if the substitute cannot be expressed
                        }
                    }
                    bindings.Add(Expression.Bind(member, value));
                }
                return Expression.MemberInit(newExpression, bindings);
            }
            finally
            {
                depth[typeMap] = current;
            }
        }

        private Expression? BuildCollection(Expression source, Type destinationType, Dictionary<TypeMap, int> depth)
        {
            var sourceElement = ReflectionHelper.GetElementType(source.Type);
            var destElement = ReflectionHelper.GetElementType(destinationType);
            var item = Expression.Parameter(sourceElement, "item");
            var elementBody = BuildValue(item, destElement, depth);
            if (elementBody == null) return null;
            if (elementBody.Type != destElement) elementBody = Expression.Convert(elementBody, destElement);

            var sourceEnumerable = typeof(IEnumerable<>).MakeGenericType(sourceElement);
            Expression sequence = sourceEnumerable.IsAssignableFrom(source.Type) ? source : Expression.Convert(source, sourceEnumerable);
            if (!ReferenceEquals(elementBody, item))
                sequence = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), new[] { sourceElement, destElement }, sequence, Expression.Lambda(elementBody, item));

            Expression result;
            var listType = typeof(List<>).MakeGenericType(destElement);
            var destEnumerable = typeof(IEnumerable<>).MakeGenericType(destElement);
            if (destinationType.IsArray)
            {
                result = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { destElement }, sequence);
            }
            else if (destinationType.IsAssignableFrom(listType))
            {
                result = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { destElement }, sequence);
            }
            else
            {
                var ctor = destinationType.GetConstructor(new[] { destEnumerable });
                if (ctor == null && destinationType.IsInterface)
                {
                    var hashSet = typeof(HashSet<>).MakeGenericType(destElement);
                    if (destinationType.IsAssignableFrom(hashSet)) ctor = hashSet.GetConstructor(new[] { destEnumerable });
                }
                if (ctor == null) throw new MappingException("Unable to project a collection to " + destinationType.FullName + ".");
                result = Expression.New(ctor, sequence);
            }
            return result.Type == destinationType ? result : Expression.Convert(result, destinationType);
        }

        private static MappingException NotProjectable(TypeMap typeMap, string member) =>
            new MappingException(typeMap + ", " + member + ": this member uses a delegate, resolver or value converter, which cannot be translated by ProjectTo. Use MapFrom(src => ...) with an expression, or Ignore() it.");
    }
}
