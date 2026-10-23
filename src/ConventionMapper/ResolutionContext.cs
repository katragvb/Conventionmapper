using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ConventionMapper.Execution;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    /// <summary>Per-call options: <c>mapper.Map&lt;Dto&gt;(src, opt =&gt; opt.Items["UserId"] = 42);</c></summary>
    public interface IMappingOperationOptions
    {
        IDictionary<string, object> Items { get; }
        void BeforeMap(Action<object, object> beforeFunction);
        void AfterMap(Action<object, object> afterFunction);
        void ConstructServicesUsing(Func<Type, object> constructor);

        /// <summary>Leave destination members untouched when their source value is null (PATCH semantics).</summary>
        void IgnoreNullSourceValues();
    }

    internal sealed class MappingOperationOptions : IMappingOperationOptions
    {
        private Dictionary<string, object>? _items;

        public IDictionary<string, object> Items => _items ??= new Dictionary<string, object>(StringComparer.Ordinal);
        internal Dictionary<string, object>? RawItems => _items;
        internal Action<object, object>? BeforeMapAction { get; private set; }
        internal Action<object, object>? AfterMapAction { get; private set; }
        internal Func<Type, object>? ServiceCtor { get; private set; }

        public void BeforeMap(Action<object, object> beforeFunction) => BeforeMapAction = beforeFunction;
        public void AfterMap(Action<object, object> afterFunction) => AfterMapAction = afterFunction;
        public void ConstructServicesUsing(Func<Type, object> constructor) => ServiceCtor = constructor;
        internal bool IgnoreNulls { get; private set; }
        public void IgnoreNullSourceValues() => IgnoreNulls = true;
    }

    /// <summary>State for a single top-level Map call; passed to resolvers, converters and actions.</summary>
    public sealed class ResolutionContext
    {
        private readonly MappingEngine _engine;
        private Func<Type, object> _serviceCtor;
        private Dictionary<string, object>? _items;
        private Dictionary<string, object>? _ownItems; // created on demand by Items, kept across pooled calls
        private Dictionary<ReferenceKey, object>? _cache;
        private Dictionary<TypeMap, int>? _depths;
        private IMapper? _mapper;

        internal ResolutionContext(MappingEngine engine, Func<Type, object> serviceCtor, Dictionary<string, object>? items)
        {
            _engine = engine;
            _serviceCtor = serviceCtor;
            _items = items;
        }

        // ---- pooling ----------------------------------------------------------------------------
        // One idle context per thread. Rent takes it (so re-entrant Map calls on the same thread simply
        // allocate a new one), Return resets it and puts it back. No locks, no shared state between threads.
        [ThreadStatic] private static ResolutionContext? t_idle;

        private const int MaxPooledEntries = 256;

        internal static ResolutionContext Rent(MappingEngine engine, Func<Type, object> serviceCtor, Dictionary<string, object>? items)
        {
            var context = t_idle;
            if (context != null && ReferenceEquals(context._engine, engine))
            {
                t_idle = null;
                context._serviceCtor = serviceCtor;
                context._items = items;
                return context;
            }
            return new ResolutionContext(engine, serviceCtor, items);
        }

        internal static void Return(ResolutionContext context)
        {
            context.Reset();
            t_idle = context;
        }

        private void Reset()
        {
            _items = null;
            if (_ownItems != null)
            {
                if (_ownItems.Count > MaxPooledEntries) _ownItems = null;
                else _ownItems.Clear();
            }
            _serviceCtor = null!;
            if (_cache != null)
            {
                if (_cache.Count > MaxPooledEntries) _cache = null;
                else _cache.Clear();
            }
            if (_depths != null)
            {
                if (_depths.Count > MaxPooledEntries) _depths = null;
                else _depths.Clear();
            }
            _path?.Clear();
            RootFramePending = false;
            IgnoreNullSourceValues = false;
            RootType = null;
            RootBeforeMap = null;
            RootAfterMap = null;
        }

        /// <summary>Items passed via <c>opt.Items</c>.</summary>
        public IDictionary<string, object> Items => _items ??= (_ownItems ??= new Dictionary<string, object>(StringComparer.Ordinal));

        /// <summary>A mapper that shares this context (items, reference cache, depth tracking).</summary>
        public IMapper Mapper => _mapper ??= new ContextMapper(_engine, this);

        /// <summary>Creates a resolver/converter/action instance using the configured service constructor (DI-aware).</summary>
        public object CreateService(Type serviceType)
        {
            var instance = _serviceCtor(serviceType);
            if (instance == null) throw new MappingException("Cannot create an instance of " + serviceType.FullName + ".");
            return instance;
        }

        internal bool RootFramePending { get; set; }
        internal bool IgnoreNullSourceValues { get; set; }
        /// <summary>Destination type of the top-level call; its display name is only computed for error paths.</summary>
        internal Type? RootType { get; set; }

        private List<string>? _path;

        internal void PushPath(string segment) => (_path ??= new List<string>()).Add(segment);

        internal void PopPath()
        {
            if (_path != null && _path.Count > 0) _path.RemoveAt(_path.Count - 1);
        }

        /// <summary>e.g. "OrderDto.Lines[3].Product".</summary>
        internal string FormatPath()
        {
            var sb = new StringBuilder(RootType == null ? string.Empty : ReflectionHelper.FriendlyName(RootType));
            if (_path != null)
            {
                foreach (var segment in _path)
                {
                    if (segment.StartsWith("[", StringComparison.Ordinal))
                    {
                        sb.Append(segment);
                        continue;
                    }
                    if (sb.Length > 0) sb.Append('.');
                    sb.Append(segment);
                }
            }
            return sb.ToString();
        }
        internal Action<object, object>? RootBeforeMap { get; set; }
        internal Action<object, object>? RootAfterMap { get; set; }

        internal bool TryGetCached(object source, Type destinationType, out object? destination)
        {
            if (_cache != null && _cache.TryGetValue(new ReferenceKey(source, destinationType), out var found))
            {
                destination = found;
                return true;
            }
            destination = null;
            return false;
        }

        internal void Cache(object source, Type destinationType, object destination)
        {
            _cache ??= new Dictionary<ReferenceKey, object>();
            _cache[new ReferenceKey(source, destinationType)] = destination;
        }

        internal int EnterDepth(TypeMap typeMap)
        {
            _depths ??= new Dictionary<TypeMap, int>();
            _depths.TryGetValue(typeMap, out var depth);
            depth++;
            _depths[typeMap] = depth;
            return depth;
        }

        internal void ExitDepth(TypeMap typeMap)
        {
            if (_depths != null && _depths.TryGetValue(typeMap, out var depth)) _depths[typeMap] = depth - 1;
        }

        internal void MergeItems(MappingOperationOptions? options)
        {
            if (options?.RawItems == null) return;
            foreach (var pair in options.RawItems) Items[pair.Key] = pair.Value;
        }
    }

    internal readonly struct ReferenceKey : IEquatable<ReferenceKey>
    {
        public ReferenceKey(object source, Type destinationType)
        {
            Source = source;
            DestinationType = destinationType;
        }

        public object Source { get; }
        public Type DestinationType { get; }

        public bool Equals(ReferenceKey other) => ReferenceEquals(Source, other.Source) && DestinationType == other.DestinationType;
        public override bool Equals(object? obj) => obj is ReferenceKey other && Equals(other);
        public override int GetHashCode() => unchecked((RuntimeHelpers.GetHashCode(Source) * 397) ^ DestinationType.GetHashCode());
    }

    /// <summary>IMapper bound to an in-flight ResolutionContext (used inside resolvers/converters).</summary>
    internal sealed class ContextMapper : IMapper
    {
        private readonly MappingEngine _engine;
        private readonly ResolutionContext _context;

        public ContextMapper(MappingEngine engine, ResolutionContext context)
        {
            _engine = engine;
            _context = context;
        }

        public IConfigurationProvider ConfigurationProvider => _engine.Configuration;

        private object? Run(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions>? opts)
        {
            if (opts != null)
            {
                var options = new MappingOperationOptions();
                opts(options);
                _context.MergeItems(options);
            }
            return _engine.Map(source, sourceType, destinationType, destination, _context, _engine.Configuration.RootProfile);
        }

        private static T As<T>(object? value) => value == null ? default! : (T)value;

        public TDestination Map<TDestination>(object? source) => As<TDestination>(Run(source, null, source?.GetType() ?? typeof(object), typeof(TDestination), null));
        public TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions> opts) => As<TDestination>(Run(source, null, source?.GetType() ?? typeof(object), typeof(TDestination), opts));
        public TDestination Map<TSource, TDestination>(TSource source) => As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), null));
        public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions> opts) => As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), opts));
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) => As<TDestination>(Run(source, destination, typeof(TSource), typeof(TDestination), null));
        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions> opts) => As<TDestination>(Run(source, destination, typeof(TSource), typeof(TDestination), opts));
        public object? Map(object? source, Type sourceType, Type destinationType) => Run(source, null, sourceType, destinationType, null);
        public object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => Run(source, null, sourceType, destinationType, opts);
        public object? Map(object? source, object? destination, Type sourceType, Type destinationType) => Run(source, destination, sourceType, destinationType, null);
        public object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) => Run(source, destination, sourceType, destinationType, opts);
        public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source) => source.ProjectTo<TDestination>(_engine.Configuration);
    }
}
