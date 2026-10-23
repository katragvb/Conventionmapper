using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    public static class QueryableExtensions
    {
        /// <summary>
        /// <c>db.Orders.ProjectTo&lt;OrderDto&gt;(config)</c> — builds a Select expression from the configured maps so
        /// only the needed columns are queried. Only expression-based configuration is translatable.
        /// </summary>
        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IConfigurationProvider configuration) =>
            Project<TDestination>(source, configuration, null, Array.Empty<string>());

        /// <summary>
        /// ProjectTo with query parameters and explicit expansion:
        /// <c>db.Posts.ProjectTo&lt;PostDto&gt;(config, new { currentUser = name }, p =&gt; p.Comments)</c>.
        /// Parameter names match variables captured in MapFrom expressions; expanded members are those configured
        /// with ExplicitExpansion(). Nested paths: <c>p =&gt; p.Author.Books</c> or <c>p =&gt; p.Comments.Select(c =&gt; c.Replies)</c>.
        /// </summary>
        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IConfigurationProvider configuration,
            object? parameters, params Expression<Func<TDestination, object>>[] membersToExpand) =>
            Project<TDestination>(source, configuration, parameters, (membersToExpand ?? Array.Empty<Expression<Func<TDestination, object>>>()).Select(MemberPath).ToArray());

        /// <summary>ProjectTo with parameters as a dictionary and expanded members as dotted paths ("Author.Books").</summary>
        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IConfigurationProvider configuration,
            IDictionary<string, object> parameters, params string[] membersToExpand) =>
            Project<TDestination>(source, configuration, parameters, membersToExpand ?? Array.Empty<string>());

        public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IMapper mapper) =>
            source.ProjectTo<TDestination>((mapper ?? throw new ArgumentNullException(nameof(mapper))).ConfigurationProvider);

        private static IQueryable<TDestination> Project<TDestination>(IQueryable source, IConfigurationProvider configuration, object? parameters, string[] membersToExpand)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var lambda = configuration.BuildProjection(source.ElementType, typeof(TDestination), parameters, membersToExpand);
            var call = Expression.Call(typeof(Queryable), nameof(Queryable.Select), new[] { source.ElementType, typeof(TDestination) }, source.Expression, Expression.Quote(lambda));
            return source.Provider.CreateQuery<TDestination>(call);
        }

        private static string MemberPath<TDestination>(Expression<Func<TDestination, object>> expression)
        {
            var segments = new List<string>();
            if (!Collect(expression.Body, expression.Parameters[0], segments) || segments.Count == 0)
                throw new ArgumentException("Members to expand must be member paths such as d => d.Orders or d => d.Orders.Select(o => o.Lines): " + expression, nameof(expression));
            return string.Join(".", segments);
        }

        private static bool Collect(Expression expression, ParameterExpression root, List<string> segments)
        {
            expression = ExpressionHelper.StripConvert(expression);
            if (expression == root) return true;
            if (expression is MemberExpression member && member.Expression != null)
            {
                if (!Collect(member.Expression, root, segments)) return false;
                segments.Add(member.Member.Name);
                return true;
            }
            if (expression is MethodCallExpression call && call.Method.Name == nameof(Enumerable.Select) &&
                call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2 && call.Arguments[1] is LambdaExpression inner)
            {
                return Collect(call.Arguments[0], root, segments) && Collect(inner.Body, inner.Parameters[0], segments);
            }
            return false;
        }
    }

    /// <summary>Replaces variables captured in MapFrom expressions with query parameters (EF Core parameterizes them).</summary>
    internal static class ProjectionParameters
    {
        public static LambdaExpression Bind(LambdaExpression lambda, object parameters)
        {
            var values = ToDictionary(parameters);
            return values.Count == 0 ? lambda : (LambdaExpression)new Binder(values).Visit(lambda)!;
        }

        private static Dictionary<string, object?> ToDictionary(object parameters)
        {
            if (parameters is IDictionary<string, object> dictionary)
                return dictionary.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
            return parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() != null)
                .ToDictionary(p => p.Name, p => p.GetValue(parameters, null), StringComparer.Ordinal);
        }

        private sealed class Binder : ExpressionVisitor
        {
            private readonly Dictionary<string, object?> _values;

            public Binder(Dictionary<string, object?> values) { _values = values; }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression is ConstantExpression constant && constant.Value != null &&
                    constant.Type.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                    _values.TryGetValue(node.Member.Name, out var value))
                {
                    return QueryParameter.Create(node.Type, value);
                }
                return base.VisitMember(node);
            }
        }
    }

    internal static class QueryParameter
    {
        public static Expression Create(Type type, object? value)
        {
            if (value != null && !type.IsInstanceOfType(value))
                value = Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type, CultureInfo.InvariantCulture);
            if (value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                value = Activator.CreateInstance(type);
            var holderType = typeof(QueryParameter<>).MakeGenericType(type);
            var holder = Activator.CreateInstance(holderType)!;
            holderType.GetField(nameof(QueryParameter<int>.Value))!.SetValue(holder, value);
            return Expression.Field(Expression.Constant(holder), nameof(QueryParameter<int>.Value));
        }
    }

    /// <summary>Closure-like holder: query providers treat a field read on a constant as a parameter.</summary>
    internal sealed class QueryParameter<T>
    {
        public T Value = default!;
    }

    internal sealed class ProjectionBuilder
    {
        private readonly MapperConfiguration _configuration;
        private readonly HashSet<string> _expand;

        public ProjectionBuilder(MapperConfiguration configuration, IEnumerable<string>? expand = null)
        {
            _configuration = configuration;
            _expand = new HashSet<string>(expand ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public LambdaExpression Build(Type sourceType, Type destinationType)
        {
            var parameter = Expression.Parameter(sourceType, "src");
            var body = BuildValue(parameter, destinationType, new Dictionary<TypeMap, int>(), string.Empty, true)
                       ?? throw new MappingException("Unable to build a projection from " + sourceType.FullName + " to " + destinationType.FullName + ".");
            if (body.Type != destinationType) body = Expression.Convert(body, destinationType);
            return Expression.Lambda(typeof(Func<,>).MakeGenericType(sourceType, destinationType), body, parameter);
        }

        private Expression? BuildValue(Expression source, Type destinationType, Dictionary<TypeMap, int> depth, string path, bool topLevel = false)
        {
            var typeMap = _configuration.ResolveTypeMap(source.Type, source.Type, destinationType);
            if (typeMap != null)
            {
                var body = BuildTypeMap(typeMap, source, depth, path);
                if (body == null) return null;
                if (body.Type != destinationType) body = Expression.Convert(body, destinationType);
                if (!topLevel && (!source.Type.IsValueType || Nullable.GetUnderlyingType(source.Type) != null))
                    return Expression.Condition(Expression.Equal(source, Expression.Constant(null, source.Type)), Expression.Default(destinationType), body);
                return body;
            }

            if (destinationType == source.Type) return source;

            if (destinationType != typeof(string) && source.Type != typeof(string) &&
                ReflectionHelper.IsCollectionType(destinationType) && ReflectionHelper.IsCollectionType(source.Type))
                return BuildCollection(source, destinationType, depth, path);

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

        private Expression? BuildTypeMap(TypeMap typeMap, Expression source, Dictionary<TypeMap, int> depth, string path)
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
                        var parameterPath = Combine(path, p.Parameter.Name ?? string.Empty);
                        var arg = BuildValue(sourceExpression, p.ParameterType, depth, parameterPath) ?? Expression.Default(p.ParameterType);
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

                    var memberPath = Combine(path, map.DestinationName);
                    if (map.ExplicitExpansion && !_expand.Contains(memberPath)) continue;

                    var sourceExpression = map.Source!.BuildProjection(source) ?? throw NotProjectable(typeMap, map.DestinationName);
                    var value = BuildValue(sourceExpression, map.DestinationType, depth, memberPath);
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

        private Expression? BuildCollection(Expression source, Type destinationType, Dictionary<TypeMap, int> depth, string path)
        {
            var sourceElement = ReflectionHelper.GetElementType(source.Type);
            var destElement = ReflectionHelper.GetElementType(destinationType);
            var item = Expression.Parameter(sourceElement, "item");
            var elementBody = BuildValue(item, destElement, depth, path);
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

        private static string Combine(string path, string name) => path.Length == 0 ? name : path + "." + name;

        private static MappingException NotProjectable(TypeMap typeMap, string member) =>
            new MappingException(typeMap + ", " + member + ": this member uses a delegate, resolver or value converter, which cannot be translated by ProjectTo. Use MapFrom(src => ...) with an expression, or Ignore() it.");
    }
}
