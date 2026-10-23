using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using ConventionMapper.Internal;

namespace ConventionMapper.Execution
{
    internal sealed partial class MapCompiler
    {
        private CompiledMap? TryBuildInlined(TypeMap typeMap)
        {
            try
            {
                return new InlineBuilder(this, typeMap).Build();
            }
            catch (Exception)
            {
                return null; // keep the regular compiled delegate
            }
        }

        /// <summary>
        /// Builds the fast-path delegate of a context-free map with everything expanded in place:
        ///   dest.Customer = src.Customer == null ? null : new CustomerDto { Name = src.Customer.Name };
        ///   for (var i = 0; i &lt; src.Lines.Count; i++) list.Add(new LineDto { ... });
        /// No object-typed delegate calls, no per-element helper calls, no boxed enumerators.
        /// Semantics match the regular compiled map (existing destination objects and collections are reused,
        /// null collections become empty unless AllowNullCollections). The fast path runs without a context, and any
        /// failure is re-run on the regular path, so error messages still contain the full member path.
        /// </summary>
        private sealed class InlineBuilder
        {
            private const int MaxInlineDepth = 4;

            private static readonly MethodInfo CastMethod = typeof(CastHelper).GetMethod(nameof(CastHelper.Cast))!;
            private static readonly MethodInfo InvokeNestedMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.InvokeNested))!;
            private static readonly MethodInfo MemberFailedMethod = typeof(MappingEngine).GetMethod(nameof(MappingEngine.MemberFailed))!;
            private static readonly MethodInfo MoveNextMethod = typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext))!;
            private static readonly MethodInfo DisposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;

            private readonly MapCompiler _owner;
            private readonly TypeMap _root;
            private readonly HashSet<TypeMap> _inlining = new HashSet<TypeMap>();

            public InlineBuilder(MapCompiler owner, TypeMap root)
            {
                _owner = owner;
                _root = root;
            }

            public CompiledMap? Build()
            {
                if (!IsInlinable(_root)) return null;

                var srcObj = Expression.Parameter(typeof(object), "source");
                var destObj = Expression.Parameter(typeof(object), "destination");
                var ctx = Expression.Parameter(typeof(ResolutionContext), "context");
                var src = Expression.Variable(_root.SourceType, "src");
                var dest = Expression.Variable(_root.DestinationType, "dest");
                var step = Expression.Variable(typeof(int), "step");

                var create = CreateNew(_root, src);
                if (create == null) return null;

                _inlining.Add(_root);
                var names = new List<string>();
                var body = new List<Expression>
                {
                    Expression.Assign(src, Expression.Convert(srcObj, _root.SourceType)),
                    Expression.Assign(dest, Expression.Condition(
                        Expression.Equal(destObj, Expression.Constant(null)),
                        create,
                        Expression.Convert(destObj, _root.DestinationType)))
                };
                foreach (var map in _root.OrderedPropertyMaps)
                {
                    var member = Member(_root, map, src, dest, 0);
                    if (member == null) return null;
                    body.Add(Expression.Assign(step, Expression.Constant(names.Count)));
                    names.Add(map.DestinationName);
                    body.Add(member);
                }
                body.Add(Expression.Assign(step, Expression.Constant(-1)));
                body.Add(Expression.Empty());

                var exception = Expression.Variable(typeof(Exception), "ex");
                var guarded = Expression.TryCatch(
                    Expression.Block(typeof(void), body),
                    Expression.Catch(exception, Expression.Throw(
                        Expression.Call(MemberFailedMethod, Expression.Constant(_root), Expression.Constant(names.ToArray()), step, exception,
                            Expression.Constant(null, typeof(ResolutionContext))),
                        typeof(void))));

                var block = Expression.Block(typeof(object), new[] { src, dest, step },
                    Expression.Assign(step, Expression.Constant(-1)),
                    guarded,
                    Expression.Convert(dest, typeof(object)));
                return Expression.Lambda<CompiledMap>(block, srcObj, destObj, ctx).Compile();
            }

            private static bool IsInlinable(TypeMap t) =>
                CanCompile(t) && t.PathMaps.Length == 0 && t.BeforeMaps.Length == 0 && t.AfterMaps.Length == 0;

            /// <summary>new TDest(...) without the engine, or null when construction needs runtime services.</summary>
            private Expression? CreateNew(TypeMap t, Expression src)
            {
                if (t.CustomCtor != null) return null;
                var ctorMap = t.ConstructorMap;
                if (ctorMap != null)
                {
                    if (!ctorMap.Constructor.IsPublic) return null;
                    var args = new List<Expression>();
                    foreach (var p in ctorMap.Parameters)
                    {
                        var arg = p.Source == null ? Builder.DefaultArgument(p) : _owner.TryDirect(p.Source, src, p.ParameterType, t.Profile);
                        if (arg == null) return null;
                        args.Add(arg);
                    }
                    return Expression.New(ctorMap.Constructor, args);
                }
                var ctor = t.DestinationType.GetConstructor(Type.EmptyTypes);
                return ctor == null ? null : Expression.New(ctor);
            }

            /// <summary>The assignment for one destination member, or null when it cannot be inlined.</summary>
            private Expression? Member(TypeMap t, PropertyMap map, Expression src, ParameterExpression dest, int depth)
            {
                if (Builder.NeedsInterpreter(map) || map.Source == null) return null;
                var member = map.DestinationMember;
                var target = Builder.MemberAccess(dest, member.Member);

                var direct = _owner.TryDirect(map.Source, src, member.Type, t.Profile);
                if (direct != null) return Expression.Assign(target, direct);

                if (!(map.Source is ChainSource chain) || !CanBuildChain(chain)) return null;
                if (member.Member is PropertyInfo property && property.GetGetMethod() == null) return null;

                return Nested(t, map, chain, src, target, member.Type, depth)
                       ?? Collection(t, chain, src, target, member.Type, depth);
            }

            // ---------------------------------------------------------- nested objects

            private Expression? Nested(TypeMap t, PropertyMap map, ChainSource chain, Expression src, Expression target, Type destinationType, int depth)
            {
                if (destinationType.IsValueType || destinationType == typeof(string) || !t.Profile.AllowNullDestinationValues) return null;
                var sourceType = chain.ValueType;
                if (sourceType.IsValueType) return null;
                var child = _owner.FindInlineTypeMap(sourceType, destinationType, out var plan);
                if (child == null || plan == null || !plan.ContextFree) return null;

                var value = Expression.Variable(sourceType, "nested");
                var existing = Expression.Variable(destinationType, "existing");
                var mapped = Into(child, value, existing, depth + 1) ?? CallDelegate(plan.Map, value, existing, destinationType, map.DestinationName);

                return Expression.Block(new[] { value, existing },
                    Expression.Assign(value, BuildChain(chain, src, sourceType)),
                    Expression.Assign(existing, target),
                    Expression.Assign(target, Expression.Condition(
                        Expression.Equal(value, Expression.Constant(null, sourceType)),
                        Expression.Constant(null, destinationType),
                        mapped)));
            }

            /// <summary>The child map expanded in place: existing ?? new TChild(...), then its members. Null when not inlinable.</summary>
            private Expression? Into(TypeMap child, Expression source, Expression? existing, int depth)
            {
                if (depth > MaxInlineDepth || _inlining.Contains(child) || !IsInlinable(child)) return null;
                _inlining.Add(child);
                try
                {
                    var src = Expression.Variable(child.SourceType, "childSrc");
                    var dest = Expression.Variable(child.DestinationType, "child");
                    var create = CreateNew(child, src);
                    if (create == null) return null;

                    var statements = new List<Expression>
                    {
                        Expression.Assign(src, To(source, child.SourceType)),
                        Expression.Assign(dest, existing == null
                            ? create
                            : Expression.Condition(Expression.Equal(existing, Expression.Constant(null, existing.Type)), create, To(existing, child.DestinationType)))
                    };
                    foreach (var map in child.OrderedPropertyMaps)
                    {
                        var member = Member(child, map, src, dest, depth);
                        if (member == null) return null;
                        statements.Add(member);
                    }
                    statements.Add(dest);
                    return Expression.Block(child.DestinationType, new[] { src, dest }, statements);
                }
                finally
                {
                    _inlining.Remove(child);
                }
            }

            private static Expression CallDelegate(CompiledMap map, Expression value, Expression? existing, Type destinationType, string name) =>
                Expression.Call(CastMethod.MakeGenericMethod(destinationType),
                    Expression.Call(InvokeNestedMethod,
                        Expression.Constant(map),
                        Expression.Convert(value, typeof(object)),
                        existing == null ? (Expression)Expression.Constant(null) : Expression.Convert(existing, typeof(object)),
                        Expression.Constant(null, typeof(ResolutionContext)),
                        Expression.Constant(name)));

            // ---------------------------------------------------------- collections

            private Expression? Collection(TypeMap t, ChainSource chain, Expression src, Expression target, Type destinationType, int depth)
            {
                var sourceType = chain.ValueType;
                if (sourceType == typeof(string) || destinationType == typeof(string)) return null;
                if (!ReflectionHelper.IsCollectionType(sourceType) || !ReflectionHelper.IsCollectionType(destinationType)) return null;
                if (ReflectionHelper.TryGetDictionaryTypes(sourceType, out _, out _) || ReflectionHelper.TryGetDictionaryTypes(destinationType, out _, out _)) return null;
                if (_owner._engine.Configuration.ResolveTypeMap(sourceType, sourceType, destinationType) != null) return null;

                var sourceElement = ReflectionHelper.GetElementType(sourceType);
                var destinationElement = ReflectionHelper.GetElementType(destinationType);
                if (!typeof(IEnumerable<>).MakeGenericType(sourceElement).IsAssignableFrom(sourceType)) return null;
                var listType = typeof(List<>).MakeGenericType(destinationElement);
                var isArray = destinationType.IsArray && destinationType.GetArrayRank() == 1;
                if (!isArray && destinationType != listType && !(destinationType.IsInterface && destinationType.IsAssignableFrom(listType))) return null;

                var item = Expression.Variable(sourceElement, "item");
                var element = Element(t, sourceElement, destinationElement, item, depth);
                if (element == null) return null;

                var items = Expression.Variable(sourceType, "items");
                var existing = Expression.Variable(destinationType, "existing");
                var reusable = Expression.Variable(typeof(ICollection<>).MakeGenericType(destinationElement), "reusable");
                var collectionType = reusable.Type;
                var clear = Expression.Call(reusable, collectionType.GetMethod(nameof(ICollection<int>.Clear))!);
                var add = collectionType.GetMethod(nameof(ICollection<int>.Add))!;

                // Same rule as the interpreter: refill an existing mutable collection (never an array) in place.
                Expression findReusable = isArray
                    ? (Expression)Expression.Assign(reusable, Expression.Constant(null, collectionType))
                    : Expression.Block(
                        Expression.Assign(reusable, Expression.TypeAs(existing, collectionType)),
                        Expression.IfThen(
                            Expression.AndAlso(
                                Expression.NotEqual(reusable, Expression.Constant(null, collectionType)),
                                Expression.Property(reusable, typeof(ICollection<>).MakeGenericType(destinationElement).GetProperty(nameof(ICollection<int>.IsReadOnly))!)),
                            Expression.Assign(reusable, Expression.Constant(null, collectionType))));

                var hasReusable = Expression.NotEqual(reusable, Expression.Constant(null, collectionType));
                Expression empty = isArray
                    ? Expression.Call(typeof(Array).GetMethod(nameof(Array.Empty))!.MakeGenericMethod(destinationElement))
                    : Expression.New(listType);
                Expression whenNull = t.Profile.AllowNullCollections
                    ? Expression.Assign(target, Expression.Constant(null, destinationType))
                    : Expression.IfThenElse(hasReusable, clear, Expression.Assign(target, To(empty, destinationType)));

                var refill = Expression.Block(clear, Loop(items, sourceType, sourceElement, item, Expression.Call(reusable, add, element)));
                var fresh = FillNew(items, sourceType, sourceElement, destinationElement, item, element, isArray, target, destinationType);

                return Expression.Block(new[] { items, existing, reusable, item },
                    Expression.Assign(items, BuildChain(chain, src, sourceType)),
                    Expression.Assign(existing, target),
                    findReusable,
                    Expression.IfThenElse(
                        Expression.Equal(items, Expression.Constant(null, sourceType)),
                        whenNull,
                        Expression.IfThenElse(hasReusable, refill, fresh)));
            }

            private Expression FillNew(ParameterExpression items, Type sourceType, Type sourceElement, Type destinationElement,
                ParameterExpression item, Expression element, bool isArray, Expression target, Type destinationType)
            {
                if (isArray && (sourceType.IsArray || IsList(sourceType, sourceElement)))
                {
                    // Indexable source to array: allocate once, assign by index.
                    var array = Expression.Variable(destinationElement.MakeArrayType(), "array");
                    var i = Expression.Variable(typeof(int), "i");
                    var count = Count(items, sourceType, sourceElement)!;
                    var done = Expression.Label("done");
                    return Expression.Block(new[] { array, i },
                        Expression.Assign(array, Expression.NewArrayBounds(destinationElement, count)),
                        Expression.Assign(i, Expression.Constant(0)),
                        Expression.Loop(
                            Expression.IfThenElse(
                                Expression.LessThan(i, Expression.ArrayLength(array)),
                                Expression.Block(
                                    Expression.Assign(item, Index(items, sourceType, i)),
                                    Expression.Assign(Expression.ArrayAccess(array, i), element),
                                    Expression.PostIncrementAssign(i)),
                                Expression.Break(done)),
                            done),
                        Expression.Assign(target, To(array, destinationType)));
                }

                var listType = typeof(List<>).MakeGenericType(destinationElement);
                var list = Expression.Variable(listType, "list");
                var size = Count(items, sourceType, sourceElement);
                var ctor = size == null ? Expression.New(listType) : Expression.New(listType.GetConstructor(new[] { typeof(int) })!, size);
                Expression result = isArray ? Expression.Call(list, listType.GetMethod(nameof(List<int>.ToArray))!) : list;
                return Expression.Block(new[] { list },
                    Expression.Assign(list, ctor),
                    Loop(items, sourceType, sourceElement, item, Expression.Call(list, listType.GetMethod(nameof(List<int>.Add))!, element)),
                    Expression.Assign(target, To(result, destinationType)));
            }

            /// <summary>Element conversion: a direct copy or the element map expanded in place.</summary>
            private Expression? Element(TypeMap t, Type sourceElement, Type destinationElement, ParameterExpression item, int depth)
            {
                if (_owner.IsDirectlyAssignable(sourceElement, destinationElement, t.Profile)) return To(item, destinationElement);
                if (destinationElement.IsValueType || destinationElement == typeof(string) || !t.Profile.AllowNullDestinationValues) return null;
                if (sourceElement.IsValueType) return null;
                var child = _owner.FindInlineTypeMap(sourceElement, destinationElement, out var plan);
                if (child == null || plan == null || !plan.ContextFree) return null;
                var mapped = Into(child, item, null, depth + 1) ?? CallDelegate(plan.Map, item, null, destinationElement, string.Empty);
                return Expression.Condition(
                    Expression.Equal(item, Expression.Constant(null, sourceElement)),
                    Expression.Constant(null, destinationElement),
                    mapped);
            }

            private static bool IsList(Type sourceType, Type element) => sourceType == typeof(List<>).MakeGenericType(element);

            private static Expression? Count(Expression items, Type sourceType, Type element)
            {
                if (sourceType.IsArray) return Expression.ArrayLength(items);
                if (IsList(sourceType, element)) return Expression.Property(items, nameof(List<int>.Count));
                var collection = typeof(ICollection<>).MakeGenericType(element);
                if (collection.IsAssignableFrom(sourceType)) return Expression.Property(Expression.Convert(items, collection), nameof(ICollection<int>.Count));
                var readOnly = typeof(IReadOnlyCollection<>).MakeGenericType(element);
                if (readOnly.IsAssignableFrom(sourceType)) return Expression.Property(Expression.Convert(items, readOnly), nameof(IReadOnlyCollection<int>.Count));
                return null;
            }

            private static Expression Index(Expression items, Type sourceType, Expression i) =>
                sourceType.IsArray ? Expression.ArrayIndex(items, i) : Expression.Property(items, "Item", i);

            /// <summary>
            /// for (i...) item = items[i]; body   for arrays and List&lt;T&gt;,
            /// otherwise foreach with the type's own (usually struct) enumerator.
            /// </summary>
            private static Expression Loop(ParameterExpression items, Type sourceType, Type element, ParameterExpression item, Expression body)
            {
                var done = Expression.Label("loopDone");
                if (sourceType.IsArray || IsList(sourceType, element))
                {
                    var i = Expression.Variable(typeof(int), "i");
                    var count = Expression.Variable(typeof(int), "count");
                    return Expression.Block(new[] { i, count },
                        Expression.Assign(i, Expression.Constant(0)),
                        Expression.Assign(count, Count(items, sourceType, element)!),
                        Expression.Loop(
                            Expression.IfThenElse(
                                Expression.LessThan(i, count),
                                Expression.Block(Expression.Assign(item, Index(items, sourceType, i)), body, Expression.PostIncrementAssign(i)),
                                Expression.Break(done)),
                            done));
                }

                var enumerable = typeof(IEnumerable<>).MakeGenericType(element);
                var getEnumerator = (sourceType.IsInterface ? null : sourceType.GetMethod(nameof(IEnumerable.GetEnumerator), Type.EmptyTypes)) ?? enumerable.GetMethod(nameof(IEnumerable.GetEnumerator))!;
                var instance = getEnumerator.DeclaringType!.IsAssignableFrom(sourceType) && !getEnumerator.DeclaringType.IsInterface
                    ? (Expression)items
                    : Expression.Convert(items, getEnumerator.DeclaringType);
                var enumeratorType = getEnumerator.ReturnType;
                var enumerator = Expression.Variable(enumeratorType, "enumerator");
                var moveNext = enumeratorType.IsInterface ? MoveNextMethod : enumeratorType.GetMethod(nameof(IEnumerator.MoveNext), Type.EmptyTypes) ?? MoveNextMethod;
                var current = enumeratorType.GetProperty(nameof(IEnumerator.Current))
                              ?? typeof(IEnumerator<>).MakeGenericType(element).GetProperty(nameof(IEnumerator.Current))!;
                Expression currentValue = current.DeclaringType!.IsAssignableFrom(enumeratorType) && (!current.DeclaringType.IsInterface || enumeratorType.IsInterface)
                    ? Expression.Property(enumerator, current)
                    : Expression.Property(Expression.Convert(enumerator, current.DeclaringType), current);

                var loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.Call(enumerator, moveNext),
                        Expression.Block(Expression.Assign(item, To(currentValue, element)), body),
                        Expression.Break(done)),
                    done);

                Expression? dispose = null;
                if (typeof(IDisposable).IsAssignableFrom(enumeratorType))
                {
                    var own = enumeratorType.IsInterface ? null : enumeratorType.GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes);
                    dispose = own != null ? Expression.Call(enumerator, own) : Expression.Call(Expression.Convert(enumerator, typeof(IDisposable)), DisposeMethod);
                }

                return Expression.Block(new[] { enumerator },
                    Expression.Assign(enumerator, Expression.Call(instance, getEnumerator)),
                    dispose == null ? (Expression)loop : Expression.TryFinally(loop, dispose));
            }
        }
    }
}
