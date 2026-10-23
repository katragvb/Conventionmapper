using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ConventionMapper.Internal;

namespace ConventionMapper.Execution
{
    /// <summary>A whole type map compiled into one delegate.</summary>
    internal delegate object CompiledMap(object source, object? destination, ResolutionContext? context);

    internal sealed class CompiledPlan
    {
        public CompiledPlan(CompiledMap map, bool contextFree)
        {
            Map = map;
            ContextFree = contextFree;
        }

        public CompiledMap Map { get; }

        /// <summary>
        /// Fully inlined variant for the context-free fast path (nested maps and collection loops expanded in place),
        /// or null. Errors are re-run on <see cref="Map"/> with a context, so messages keep their member paths.
        /// </summary>
        public CompiledMap? FastMap { get; internal set; }

        /// <summary>True when the delegate never touches the ResolutionContext, so it can run without one (fast path).</summary>
        public bool ContextFree { get; }
    }

    /// <summary>
    /// Compiles a TypeMap into a single strongly typed expression tree:
    ///   dest = new TDest(...); dest.A = src.A; dest.CustomerName = src.Customer?.Name; ...
    /// Simple members (same or assignable value types and strings) are assigned directly with no boxing.
    /// Members that need runtime services (nested maps, collections, resolvers, conditions, ...) call back into
    /// the interpreter for that member only, so behaviour is identical to the interpreted engine.
    /// Maps that cannot be compiled (reference tracking, MaxDepth, struct destinations, ...) stay interpreted.
    /// </summary>
    internal sealed partial class MapCompiler
    {
        private static readonly MethodInfo MapNestedMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.MapNested))!;
        private static readonly MethodInfo MapPropertyMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.MapProperty))!;
        private static readonly MethodInfo CreateDestinationMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.CreateDestination))!;
        private static readonly MethodInfo RunPathsMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.RunPaths))!;
        private static readonly MethodInfo MemberFailedMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.MemberFailed))!;
        private static readonly MethodInfo ResolveMethod = typeof(ValueSource).GetMethod(nameof(ValueSource.Resolve))!;
        private static readonly MethodInfo CastMethod = typeof(CastHelper).GetMethod(nameof(CastHelper.Cast))!;
        private static readonly MethodInfo InvokeNestedMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.InvokeNested))!;
        private static readonly MethodInfo CollectionMethod = typeof(CompiledCollections).GetMethod(nameof(CompiledCollections.MapCollection))!;
        private static readonly MethodInfo NestedElementMethod = typeof(CompiledCollections).GetMethod(nameof(CompiledCollections.NestedElement))!;

        private readonly MappingEngine _engine;

        public MapCompiler(MappingEngine engine) { _engine = engine; }

        public CompiledPlan? TryCompile(TypeMap typeMap)
        {
            if (!CanCompile(typeMap)) return null;
            try
            {
                var plan = new Builder(this, typeMap).Build();
                if (plan.ContextFree) plan.FastMap = TryBuildInlined(typeMap);
                return plan;
            }
            catch (Exception)
            {
                return null; // anything unusual: stay on the interpreted path
            }
        }

        private static bool CanCompile(TypeMap t) =>
            t.TypeConverter == null && !t.PreserveReferences && t.MaxDepth == 0 && !t.IgnoreNullSourceValues &&
            t.DestinationType.IsClass && !t.DestinationType.IsAbstract &&
            !t.SourceType.ContainsGenericParameters && !t.DestinationType.ContainsGenericParameters;

        // ------------------------------------------------------------------ shared helpers

        private Expression? TryDirect(ValueSource source, Expression src, Type targetType, ProfileMap profile)
        {
            if (!(source is ChainSource chain) || !CanBuildChain(chain)) return null;
            if (!IsDirectlyAssignable(chain.ValueType, targetType, profile)) return null;
            return BuildChain(chain, src, targetType);
        }

        /// <summary>
        /// The compiled plan of the map the engine would pick for (sourceType, destinationType) at runtime, when that
        /// choice cannot depend on the runtime type (no derived maps, no maps for subtypes of the source).
        /// </summary>
        private CompiledPlan? FindInlinePlan(Type sourceType, Type destinationType) => FindInlineTypeMap(sourceType, destinationType, out var plan) == null ? null : plan;

        private TypeMap? FindInlineTypeMap(Type sourceType, Type destinationType, out CompiledPlan? plan)
        {
            plan = null;
            var configuration = _engine.Configuration;
            var nested = configuration.ResolveTypeMap(sourceType, sourceType, destinationType);
            if (nested == null || nested.DestinationType != destinationType || nested.DerivedMaps.Count > 0 || nested.EqualityComparison != null) return null;
            if (configuration.HasPotentialSubtypeMaps(sourceType, destinationType)) return null;
            plan = nested.GetCompiledPlan(this);
            return plan == null ? null : nested;
        }

        /// <summary>Func&lt;TSourceElement, ResolutionContext, TDestinationElement&gt; for inlined collections, or null.</summary>
        private Delegate? BuildElementMapper(Type sourceElement, Type destinationElement, ProfileMap profile, out bool contextFree)
        {
            contextFree = true;
            if (IsDirectlyAssignable(sourceElement, destinationElement, profile))
            {
                var item = Expression.Parameter(sourceElement, "item");
                var context = Expression.Parameter(typeof(ResolutionContext), "context");
                var funcType = typeof(Func<,,>).MakeGenericType(sourceElement, typeof(ResolutionContext), destinationElement);
                return Expression.Lambda(funcType, To(item, destinationElement), item, context).Compile();
            }
            if (destinationElement.IsValueType || destinationElement == typeof(string) || !profile.AllowNullDestinationValues) return null;
            var plan = FindInlinePlan(sourceElement, destinationElement);
            if (plan == null) return null;
            contextFree = plan.ContextFree;
            return (Delegate)NestedElementMethod.MakeGenericMethod(sourceElement, destinationElement).Invoke(null, new object[] { plan.Map })!;
        }

        /// <summary>Only value types and strings that need no conversion and have no custom map are copied directly.</summary>
        private bool IsDirectlyAssignable(Type valueType, Type targetType, ProfileMap profile)
        {
            if (!(targetType.IsValueType || targetType == typeof(string))) return false;
            if (targetType == typeof(string) && !profile.AllowNullDestinationValues) return false;
            if (!targetType.IsAssignableFrom(valueType)) return false;
            return _engine.Configuration.ResolveTypeMap(valueType, valueType, targetType) == null;
        }

        private static bool CanBuildChain(ChainSource chain)
        {
            var members = chain.Members;
            for (var i = 0; i < members.Length; i++)
            {
                if (members[i].DeclaringType == null) return false;
                if (i < members.Length - 1 && Nullable.GetUnderlyingType(ReflectionHelper.GetMemberType(members[i])) != null) return false;
            }
            return true;
        }

        /// <summary>Null-safe member chain: any null intermediate yields default(resultType), like the interpreter.</summary>
        private static Expression BuildChain(ChainSource chain, Expression src, Type resultType)
        {
            var members = chain.Members;
            if (members.Length == 0) return To(src, resultType);
            if (members.Length == 1) return To(Access(src, members[0]), resultType);

            var variables = new List<ParameterExpression>();
            var statements = new List<Expression>();
            var done = Expression.Label(resultType, "chainResult");
            Expression current = src;
            for (var i = 0; i < members.Length; i++)
            {
                var access = Access(current, members[i]);
                if (i == members.Length - 1)
                {
                    statements.Add(Expression.Return(done, To(access, resultType)));
                    break;
                }
                var temp = Expression.Variable(access.Type, "v" + i);
                variables.Add(temp);
                statements.Add(Expression.Assign(temp, access));
                if (!temp.Type.IsValueType)
                    statements.Add(Expression.IfThen(Expression.Equal(temp, Expression.Constant(null, temp.Type)), Expression.Return(done, Expression.Default(resultType))));
                current = temp;
            }
            statements.Add(Expression.Label(done, Expression.Default(resultType)));
            return Expression.Block(resultType, variables, statements);
        }

        private static Expression Access(Expression instance, MemberInfo member)
        {
            var declaring = member.DeclaringType!;
            if (declaring.IsInterface ? instance.Type != declaring : !declaring.IsAssignableFrom(instance.Type))
                instance = Expression.Convert(instance, declaring);
            if (member is MethodInfo method) return Expression.Call(instance, method);
            if (member is FieldInfo field) return Expression.Field(instance, field);
            return Expression.Property(instance, (PropertyInfo)member);
        }

        private static Expression To(Expression expression, Type type) => expression.Type == type ? expression : Expression.Convert(expression, type);

        // ------------------------------------------------------------------ per-map builder

        private sealed class Builder
        {
            private readonly MapCompiler _owner;
            private readonly TypeMap _typeMap;
            private readonly ParameterExpression _srcObj = Expression.Parameter(typeof(object), "source");
            private readonly ParameterExpression _destObj = Expression.Parameter(typeof(object), "destination");
            private readonly ParameterExpression _ctx = Expression.Parameter(typeof(ResolutionContext), "context");
            private readonly ParameterExpression _src;
            private readonly ParameterExpression _dest;
            private readonly Expression _engine;
            private bool _usesContext;

            public Builder(MapCompiler owner, TypeMap typeMap)
            {
                _owner = owner;
                _typeMap = typeMap;
                _src = Expression.Variable(typeMap.SourceType, "src");
                _dest = Expression.Variable(typeMap.DestinationType, "dest");
                _engine = Expression.Constant(owner._engine);
            }

            private Expression DestAsObject => Expression.Convert(_dest, typeof(object));

            public CompiledPlan Build()
            {
                var step = Expression.Variable(typeof(int), "step");
                var names = new List<string>();
                var body = new List<Expression>
                {
                    Expression.Assign(_src, Expression.Convert(_srcObj, _typeMap.SourceType)),
                    Expression.Assign(_dest, Expression.Condition(
                        Expression.Equal(_destObj, Expression.Constant(null)),
                        CreateDestination(),
                        Expression.Convert(_destObj, _typeMap.DestinationType)))
                };

                foreach (var action in _typeMap.BeforeMaps) body.Add(InvokeAction(action));

                foreach (var map in _typeMap.OrderedPropertyMaps)
                {
                    body.Add(Expression.Assign(step, Expression.Constant(names.Count)));
                    names.Add(map.DestinationName);
                    body.Add(BuildMember(map));
                }
                body.Add(Expression.Assign(step, Expression.Constant(-1)));

                if (_typeMap.PathMaps.Length > 0)
                {
                    _usesContext = true;
                    body.Add(Expression.Call(_engine, RunPathsMethod, Expression.Constant(_typeMap), _srcObj, DestAsObject, _ctx));
                }

                foreach (var action in _typeMap.AfterMaps) body.Add(InvokeAction(action));
                body.Add(Expression.Empty());

                // One try/catch for the whole map; "step" tells the handler which member failed.
                var exception = Expression.Variable(typeof(Exception), "ex");
                var guarded = Expression.TryCatch(
                    Expression.Block(typeof(void), body),
                    Expression.Catch(exception, Expression.Throw(
                        Expression.Call(MemberFailedMethod, Expression.Constant(_typeMap), Expression.Constant(names.ToArray()), step, exception, _ctx),
                        typeof(void))));

                var block = Expression.Block(typeof(object), new[] { _src, _dest, step },
                    Expression.Assign(step, Expression.Constant(-1)),
                    guarded,
                    Expression.Convert(_dest, typeof(object)));

                var lambda = Expression.Lambda<CompiledMap>(block, _srcObj, _destObj, _ctx);
                return new CompiledPlan(lambda.Compile(), !_usesContext);
            }

            private Expression InvokeAction(Action<object, object, ResolutionContext> action)
            {
                _usesContext = true;
                return Expression.Invoke(Expression.Constant(action), _srcObj, DestAsObject, _ctx);
            }

            private Expression CreateDestination()
            {
                var type = _typeMap.DestinationType;
                if (_typeMap.CustomCtor == null)
                {
                    var ctorMap = _typeMap.ConstructorMap;
                    if (ctorMap != null && ctorMap.Constructor.IsPublic)
                    {
                        List<Expression>? args = new List<Expression>();
                        foreach (var p in ctorMap.Parameters)
                        {
                            var arg = p.Source == null ? DefaultArgument(p) : _owner.TryDirect(p.Source, _src, p.ParameterType, _typeMap.Profile);
                            if (arg == null)
                            {
                                args = null;
                                break;
                            }
                            args.Add(arg);
                        }
                        if (args != null) return Expression.New(ctorMap.Constructor, args);
                    }
                    else if (ctorMap == null)
                    {
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor != null) return Expression.New(ctor);
                    }
                }
                _usesContext = true;
                return Expression.Convert(Expression.Call(_engine, CreateDestinationMethod, Expression.Constant(_typeMap), _srcObj, _ctx), type);
            }

            internal static Expression DefaultArgument(CtorParamMap p) =>
                p.DefaultValue == null
                    ? (Expression)Expression.Default(p.ParameterType)
                    : Expression.Convert(Expression.Constant(p.DefaultValue), p.ParameterType);

            private Expression BuildMember(PropertyMap map)
            {
                var member = map.DestinationMember;
                if (NeedsInterpreter(map))
                {
                    _usesContext = true;
                    return Expression.Call(_engine, MapPropertyMethod, Expression.Constant(_typeMap), Expression.Constant(map), _srcObj, DestAsObject, _ctx);
                }

                var target = MemberAccess(_dest, member.Member);
                var direct = _owner.TryDirect(map.Source!, _src, member.Type, _typeMap.Profile);
                if (direct != null) return Expression.Assign(target, direct);

                var inlined = TryInline(map, target);
                if (inlined != null) return inlined;

                // General case: resolve the value, let the engine map it (nested maps, collections, conversions), assign typed.
                _usesContext = true;
                var current = Expression.Variable(typeof(object), "current");
                var readable = !(member.Member is PropertyInfo property) || property.GetGetMethod() != null;
                Expression value = map.Source is ChainSource chain && CanBuildChain(chain)
                    ? BuildChain(chain, _src, typeof(object))
                    : Expression.Call(Expression.Constant(map.Source, typeof(ValueSource)), ResolveMethod, _srcObj, DestAsObject, current, _ctx);
                Expression existing = !member.Type.IsValueType && member.Type != typeof(string)
                    ? (Expression)current
                    : Expression.Constant(null, typeof(object));
                var mapped = Expression.Call(_engine, MapNestedMethod, value,
                    Expression.Constant(map.SourceValueType, typeof(Type)),
                    Expression.Constant(member.Type, typeof(Type)),
                    existing, _ctx,
                    Expression.Constant(_typeMap.Profile),
                    Expression.Constant(map.DestinationName));

                return Expression.Block(new[] { current },
                    Expression.Assign(current, readable ? Expression.Convert(target, typeof(object)) : (Expression)Expression.Constant(null, typeof(object))),
                    Expression.Assign(target, Expression.Call(CastMethod.MakeGenericMethod(member.Type), mapped)));
            }

            /// <summary>Nested objects and collections whose maps are compiled are called directly instead of through the engine.</summary>
            private Expression? TryInline(PropertyMap map, Expression target)
            {
                if (!(map.Source is ChainSource chain) || !CanBuildChain(chain)) return null;
                var member = map.DestinationMember;
                if (member.Member is PropertyInfo property && property.GetGetMethod() == null) return null;
                return TryInlineNested(map, chain, target, member.Type) ?? TryInlineCollection(map, chain, target, member.Type);
            }

            private Expression? TryInlineNested(PropertyMap map, ChainSource chain, Expression target, Type destinationType)
            {
                if (destinationType.IsValueType || destinationType == typeof(string) || !_typeMap.Profile.AllowNullDestinationValues) return null;
                var plan = _owner.FindInlinePlan(chain.ValueType, destinationType);
                if (plan == null) return null;
                if (!plan.ContextFree) _usesContext = true;
                var call = Expression.Call(InvokeNestedMethod,
                    Expression.Constant(plan.Map),
                    BuildChain(chain, _src, typeof(object)),
                    Expression.Convert(target, typeof(object)),
                    _ctx,
                    Expression.Constant(map.DestinationName));
                return Expression.Assign(target, Expression.Call(CastMethod.MakeGenericMethod(destinationType), call));
            }

            private Expression? TryInlineCollection(PropertyMap map, ChainSource chain, Expression target, Type destinationType)
            {
                var sourceType = chain.ValueType;
                if (sourceType == typeof(string) || destinationType == typeof(string)) return null;
                if (!ReflectionHelper.IsCollectionType(sourceType) || !ReflectionHelper.IsCollectionType(destinationType)) return null;
                if (ReflectionHelper.TryGetDictionaryTypes(sourceType, out _, out _) || ReflectionHelper.TryGetDictionaryTypes(destinationType, out _, out _)) return null;
                if (_owner._engine.Configuration.ResolveTypeMap(sourceType, sourceType, destinationType) != null) return null;

                var sourceElement = ReflectionHelper.GetElementType(sourceType);
                var destinationElement = ReflectionHelper.GetElementType(destinationType);
                var enumerableType = typeof(IEnumerable<>).MakeGenericType(sourceElement);
                if (!enumerableType.IsAssignableFrom(sourceType)) return null;
                var listType = typeof(List<>).MakeGenericType(destinationElement);
                var isArray = destinationType.IsArray && destinationType.GetArrayRank() == 1;
                if (!isArray && destinationType != listType && !(destinationType.IsInterface && destinationType.IsAssignableFrom(listType))) return null;

                var elementMapper = _owner.BuildElementMapper(sourceElement, destinationElement, _typeMap.Profile, out var elementContextFree);
                if (elementMapper == null) return null;
                if (!elementContextFree) _usesContext = true;

                var call = Expression.Call(CollectionMethod.MakeGenericMethod(sourceElement, destinationElement),
                    To(BuildChain(chain, _src, sourceType), enumerableType),
                    Expression.Constant(elementMapper),
                    Expression.Convert(target, typeof(object)),
                    Expression.Constant(isArray),
                    Expression.Constant(_typeMap.Profile.AllowNullCollections),
                    _ctx,
                    Expression.Constant(map.DestinationName));
                return Expression.Assign(target, Expression.Call(CastMethod.MakeGenericMethod(destinationType), call));
            }

            internal static bool NeedsInterpreter(PropertyMap map) =>
                map.PreCondition != null || map.Condition != null || map.HasNullSubstitute || map.UseDestinationValue ||
                map.Transformers.Length > 0 || !CanAssign(map.DestinationMember);

            private static bool CanAssign(DestinationMember member)
            {
                if (member.Member is PropertyInfo property)
                {
                    if (property.GetSetMethod() == null) return false;
                    var getter = property.GetGetMethod(true);
                    return getter == null || getter.IsPublic;
                }
                return member.Member is FieldInfo field && field.IsPublic && !field.IsInitOnly;
            }

            internal static Expression MemberAccess(Expression instance, MemberInfo member)
            {
                var declaring = member.DeclaringType!;
                if (!declaring.IsAssignableFrom(instance.Type)) instance = Expression.Convert(instance, declaring);
                return member is PropertyInfo property ? Expression.Property(instance, property) : Expression.Field(instance, (FieldInfo)member);
            }
        }
    }

    // ---------------------------------------------------------------------- static fast-path caches

    /// <summary>A compiled, context-free map for one source/destination pair (Map is null when the pair is not eligible).</summary>
    internal sealed class FastMapEntry
    {
        public FastMapEntry(MapperConfiguration configuration, Type sourceType, CompiledMap? map)
        {
            Configuration = configuration;
            SourceType = sourceType;
            Map = map;
        }

        public readonly MapperConfiguration Configuration;
        public readonly Type SourceType;
        public readonly CompiledMap? Map;
    }

    /// <summary>Static generic cache for mapper.Map&lt;TSource, TDestination&gt;: one field read instead of a dictionary lookup.</summary>
    internal static class FastMapCache<TSource, TDestination>
    {
        public static FastMapEntry? Entry;
    }

    /// <summary>Static generic cache for mapper.Map&lt;TDestination&gt;(object): up to 8 source types per destination.</summary>
    internal static class FastMapCache<TDestination>
    {
        public static FastMapEntry[] Entries = Array.Empty<FastMapEntry>();
        public static readonly object Sync = new object();
    }
}
