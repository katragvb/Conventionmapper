using System;
using System.Collections;
using System.Globalization;
using ConventionMapper.Internal;

namespace ConventionMapper.Execution
{
    internal sealed class MappingEngine
    {
        private readonly MapCompiler _compiler;

        public MappingEngine(MapperConfiguration configuration)
        {
            Configuration = configuration;
            _compiler = new MapCompiler(this);
        }

        public MapperConfiguration Configuration { get; }

        internal MapCompiler Compiler => _compiler;

        public object? MapRoot(object? source, Type sourceType, Type destinationType, object? destination, ResolutionContext context, MappingOperationOptions? options)
        {
            context.RootType = destinationType;
            if (options != null && options.IgnoreNulls) context.IgnoreNullSourceValues = true;

            var before = options?.BeforeMapAction;
            var after = options?.AfterMapAction;
            var profile = Configuration.RootProfile;
            if (before == null && after == null) return Map(source, sourceType, destinationType, destination, context, profile);

            if (source != null)
            {
                var typeMap = Configuration.ResolveTypeMap(source.GetType(), sourceType, destinationType);
                if (typeMap != null && typeMap.TypeConverter == null)
                {
                    context.RootFramePending = true;
                    context.RootBeforeMap = before;
                    context.RootAfterMap = after;
                    return Map(source, sourceType, destinationType, destination, context, profile);
                }
            }
            before?.Invoke(source!, destination!);
            var result = Map(source, sourceType, destinationType, destination, context, profile);
            after?.Invoke(source!, result!);
            return result;
        }

        public object? Map(object? source, Type sourceType, Type destinationType, object? destination, ResolutionContext context, ProfileMap profile)
        {
            if (source == null) return MapNull(sourceType, destinationType, destination, context, profile);

            var runtimeType = source.GetType();
            var typeMap = Configuration.ResolveTypeMap(runtimeType, sourceType, destinationType);
            if (typeMap != null)
                return MapWithTypeMap(typeMap.SelectDerived(runtimeType, destinationType, destination), source, destination, context);

            try
            {
                return MapBuiltIn(source, runtimeType, destinationType, destination, context, profile);
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            catch (Exception ex)
            {
                throw MappingException.ForTypes(runtimeType, destinationType, ex).CapturePath(context);
            }
        }

        private object? MapWithTypeMap(TypeMap typeMap, object source, object? destination, ResolutionContext context)
        {
            var isRoot = context.RootFramePending;
            context.RootFramePending = false;

            if (typeMap.TypeConverter != null)
            {
                try
                {
                    return typeMap.TypeConverter(source, destination, context);
                }
                catch (MappingException ex)
                {
                    ex.CapturePath(context);
                    throw;
                }
                catch (Exception ex)
                {
                    throw MappingException.ForTypes(typeMap.SourceType, typeMap.DestinationType, ex).CapturePath(context);
                }
            }

            // Compiled fast path (falls back to the interpreter below when the map is not compilable).
            if (!isRoot && !context.IgnoreNullSourceValues && Configuration.UseCompiledMappings)
            {
                var plan = typeMap.GetCompiledPlan(_compiler);
                if (plan != null)
                {
                    try
                    {
                        return plan.Map(source, destination, context);
                    }
                    catch (MappingException ex)
                    {
                        ex.CapturePath(context);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw MappingException.ForTypes(typeMap.SourceType, typeMap.DestinationType, ex).CapturePath(context);
                    }
                }
            }

            var track = typeMap.PreserveReferences && !source.GetType().IsValueType;
            if (track && context.TryGetCached(source, typeMap.DestinationType, out var cached)) return cached;

            var depthTracked = typeMap.MaxDepth > 0;
            if (depthTracked && context.EnterDepth(typeMap) > typeMap.MaxDepth)
            {
                context.ExitDepth(typeMap);
                return destination;
            }

            try
            {
                destination ??= CreateDestination(typeMap, source, context);
                if (track) context.Cache(source, typeMap.DestinationType, destination);

                if (isRoot) context.RootBeforeMap?.Invoke(source, destination);
                foreach (var action in typeMap.BeforeMaps) action(source, destination, context);
                foreach (var map in typeMap.OrderedPropertyMaps) MapProperty(typeMap, map, source, destination, context);
                foreach (var path in typeMap.PathMaps) MapPath(typeMap, path, source, destination, context);
                foreach (var action in typeMap.AfterMaps) action(source, destination, context);
                if (isRoot) context.RootAfterMap?.Invoke(source, destination);
                return destination;
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            catch (Exception ex)
            {
                throw MappingException.ForTypes(typeMap.SourceType, typeMap.DestinationType, ex).CapturePath(context);
            }
            finally
            {
                if (depthTracked) context.ExitDepth(typeMap);
            }
        }

        public object CreateDestination(TypeMap typeMap, object source, ResolutionContext context)
        {
            if (typeMap.CustomCtor != null) return typeMap.CustomCtor(source, context);

            var ctor = typeMap.ConstructorMap;
            if (ctor != null)
            {
                var args = new object?[ctor.Parameters.Length];
                for (var i = 0; i < args.Length; i++)
                {
                    var p = ctor.Parameters[i];
                    if (p.Source == null)
                    {
                        args[i] = p.DefaultValue;
                        continue;
                    }
                    context.PushPath(p.Parameter.Name ?? "ctor");
                    try
                    {
                        var value = p.Source.Resolve(source, null, null, context);
                        args[i] = Map(value, p.Source.ValueType, p.ParameterType, null, context, typeMap.Profile);
                    }
                    catch (MappingException ex)
                    {
                        ex.CapturePath(context);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw MappingException.ForMember(typeMap, "constructor parameter " + p.Parameter.Name, ex).CapturePath(context);
                    }
                    finally
                    {
                        context.PopPath();
                    }
                }
                return ctor.Invoke(args);
            }

            if (typeMap.Factory != null) return typeMap.Factory();
            throw new MappingException(
                "Cannot create an instance of " + typeMap.DestinationType.FullName + " for map " + typeMap +
                ". Add a parameterless constructor, make constructor parameters resolvable, or use ConstructUsing.",
                null, typeMap.SourceType, typeMap.DestinationType, null).CapturePath(context);
        }

        private bool SkipNull(TypeMap typeMap, ResolutionContext context) => typeMap.IgnoreNullSourceValues || context.IgnoreNullSourceValues;

        public void MapProperty(TypeMap typeMap, PropertyMap map, object source, object destination, ResolutionContext context)
        {
            context.PushPath(map.DestinationName);
            try
            {
                if (map.PreCondition != null && !map.PreCondition(source, destination, context)) return;

                var member = map.DestinationMember;
                var current = member.Getter != null ? member.Getter(destination) : null;
                var value = map.Source!.Resolve(source, destination, current, context);

                if (map.Condition != null && !map.Condition(source, destination, value, current, context)) return;
                if (value == null && map.HasNullSubstitute) value = map.NullSubstitute;
                if (value == null && SkipNull(typeMap, context)) return;

                // Existing reference-type values are mapped *into* (Map(src, dest) semantics).
                var existing = current != null && !member.Type.IsValueType && member.Type != typeof(string) ? current : null;
                if (!member.CanWrite && existing == null) return;

                var mapped = Map(value, value?.GetType() ?? map.SourceValueType, member.Type, existing, context, typeMap.Profile);
                mapped = Transform(map.Transformers, mapped);
                if (member.Setter != null) member.Setter(destination, mapped);
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            catch (Exception ex)
            {
                throw MappingException.ForMember(typeMap, map.DestinationName, ex).CapturePath(context);
            }
            finally
            {
                context.PopPath();
            }
        }

        private void MapPath(TypeMap typeMap, PathMap path, object source, object destination, ResolutionContext context)
        {
            var name = string.Join(".", Array.ConvertAll(path.Path, m => m.Name));
            context.PushPath(name);
            try
            {
                if (path.Condition != null && !path.Condition(source, destination)) return;
                var value = path.Source.Resolve(source, destination, null, context);
                if (value == null && SkipNull(typeMap, context)) return;

                var target = destination;
                for (var i = 0; i < path.Getters.Length; i++)
                {
                    var next = path.Getters[i](target);
                    if (next == null)
                    {
                        if (value == null) return; // don't create intermediate objects just to store null
                        var factory = path.Factories[i];
                        if (factory == null) return;
                        next = factory();
                        path.Setters[i](target, next);
                    }
                    target = next;
                }
                var mapped = Map(value, value?.GetType() ?? path.Source.ValueType, path.FinalType, null, context, typeMap.Profile);
                path.Setters[path.Setters.Length - 1](target, mapped);
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            catch (Exception ex)
            {
                throw MappingException.ForMember(typeMap, name, ex).CapturePath(context);
            }
            finally
            {
                context.PopPath();
            }
        }

        // ------------------------------------------------------------------ entry points used by compiled maps

        /// <summary>Maps one member value through the engine (nested maps, collections, conversions) with path tracking.</summary>
        public object? MapNested(object? value, Type declaredType, Type destinationType, object? existing, ResolutionContext context, ProfileMap profile, string memberName)
        {
            context.PushPath(memberName);
            try
            {
                return Map(value, value?.GetType() ?? declaredType, destinationType, existing, context, profile);
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            finally
            {
                context.PopPath();
            }
        }

        /// <summary>Calls a nested map's compiled delegate directly (used by compiled parents), tracking the member path.</summary>
        public static object? InvokeNested(CompiledMap map, object? value, object? existing, ResolutionContext? context, string memberName)
        {
            if (value == null) return null;
            if (context == null) return map(value, existing, null);
            context.PushPath(memberName);
            try
            {
                return map(value, existing, context);
            }
            catch (MappingException ex)
            {
                ex.CapturePath(context);
                throw;
            }
            finally
            {
                context.PopPath();
            }
        }

        public void RunPaths(TypeMap typeMap, object source, object destination, ResolutionContext context)
        {
            foreach (var path in typeMap.PathMaps) MapPath(typeMap, path, source, destination, context);
        }

        /// <summary>Turns an exception thrown inside a compiled map into the same MappingException the interpreter produces.</summary>
        public static Exception MemberFailed(TypeMap typeMap, string[] names, int step, Exception exception, ResolutionContext? context)
        {
            if (step < 0 || step >= names.Length)
            {
                var whole = exception as MappingException ?? MappingException.ForTypes(typeMap.SourceType, typeMap.DestinationType, exception);
                return context != null ? whole.CapturePath(context) : whole;
            }
            var member = names[step];
            if (context == null) return exception as MappingException ?? MappingException.ForMember(typeMap, member, exception);
            context.PushPath(member);
            try
            {
                var mapped = exception as MappingException ?? MappingException.ForMember(typeMap, member, exception);
                return mapped.CapturePath(context);
            }
            finally
            {
                context.PopPath();
            }
        }

        private static object? Transform(Func<object?, object?>[] transformers, object? value)
        {
            foreach (var transform in transformers) value = transform(value);
            return value;
        }

        private object? MapNull(Type sourceType, Type destinationType, object? destination, ResolutionContext context, ProfileMap profile)
        {
            if (ReflectionHelper.IsCollectionType(destinationType))
            {
                if (profile.AllowNullCollections) return null;
                if (destination != null && CollectionMapper.TryClear(destination)) return destination;
                return CollectionMapper.CreateEmpty(this, destinationType, context, profile);
            }
            if (destinationType.IsValueType)
                return Nullable.GetUnderlyingType(destinationType) != null ? null : Activator.CreateInstance(destinationType);
            if (!profile.AllowNullDestinationValues)
            {
                if (destinationType == typeof(string)) return string.Empty;
                var typeMap = Configuration.ResolveTypeMap(sourceType, sourceType, destinationType);
                if (typeMap?.Factory != null) return typeMap.Factory();
            }
            return null;
        }

        private object? MapBuiltIn(object source, Type sourceType, Type destinationType, object? destination, ResolutionContext context, ProfileMap profile)
        {
            if (destinationType == typeof(object)) return source;
            var destUnderlying = Nullable.GetUnderlyingType(destinationType) ?? destinationType;

            if (destUnderlying == typeof(string)) return source as string ?? source.ToString();
            if (source is string text && destUnderlying != destinationType && string.IsNullOrWhiteSpace(text)) return null;

            if (!(source is string) && source is IEnumerable enumerable && ReflectionHelper.IsCollectionType(destinationType))
                return CollectionMapper.Map(this, enumerable, sourceType, destinationType, destination, context, profile);

            if (destUnderlying.IsEnum) return EnumMapper.ToEnum(source, sourceType, destUnderlying);
            if (destUnderlying.IsAssignableFrom(sourceType)) return source;
            if (sourceType.IsEnum && ReflectionHelper.IsNumeric(destUnderlying)) return Convert.ChangeType(source, destUnderlying, CultureInfo.InvariantCulture);

            var converter = Conversions.Get(sourceType, destUnderlying);
            if (converter != null) return converter(source);

            throw MappingException.MissingMap(sourceType, destinationType);
        }
    }
}
