using System;
using System.Linq;
using ConventionMapper.Execution;

namespace ConventionMapper
{
    /// <summary>Executes mappings. Thread-safe; create via <c>configuration.CreateMapper()</c> or inject it.</summary>
    public interface IMapper
    {
        IConfigurationProvider ConfigurationProvider { get; }

        TDestination Map<TDestination>(object? source);
        TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions> opts);
        TDestination Map<TSource, TDestination>(TSource source);
        TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions> opts);
        TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
        TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions> opts);
        object? Map(object? source, Type sourceType, Type destinationType);
        object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts);
        object? Map(object? source, object? destination, Type sourceType, Type destinationType);
        object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts);

        /// <summary>Projects an IQueryable using the configured maps (translatable by EF Core and other LINQ providers).</summary>
        IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source);
    }

    public sealed class Mapper : IMapper
    {
        private readonly MapperConfiguration _configuration;
        private readonly Func<Type, object> _serviceCtor;

        public Mapper(IConfigurationProvider configurationProvider) : this(configurationProvider, null) { }

        public Mapper(IConfigurationProvider configurationProvider, Func<Type, object>? serviceCtor)
        {
            _configuration = configurationProvider as MapperConfiguration
                             ?? throw new ArgumentException("The configuration provider must be a MapperConfiguration.", nameof(configurationProvider));
            _serviceCtor = serviceCtor ?? _configuration.ServiceCtor ?? DefaultServiceCtor;
        }

        public IConfigurationProvider ConfigurationProvider => _configuration;

        private static object DefaultServiceCtor(Type type) => Activator.CreateInstance(type, true)!;

        private static T As<T>(object? value) => value == null ? default! : (T)value;

        private object? Run(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions>? opts)
        {
            if (sourceType == null) throw new ArgumentNullException(nameof(sourceType));
            if (destinationType == null) throw new ArgumentNullException(nameof(destinationType));
            MappingOperationOptions? options = null;
            if (opts != null)
            {
                options = new MappingOperationOptions();
                opts(options);
            }
            var engine = _configuration.Engine;
            var serviceCtor = options?.ServiceCtor ?? _serviceCtor;
            if (!_configuration.UseContextPooling)
                return engine.MapRoot(source, sourceType, destinationType, destination, new ResolutionContext(engine, serviceCtor, options?.RawItems), options);

            var context = ResolutionContext.Rent(engine, serviceCtor, options?.RawItems);
            try
            {
                return engine.MapRoot(source, sourceType, destinationType, destination, context, options);
            }
            finally
            {
                ResolutionContext.Return(context);
            }
        }

        public TDestination Map<TDestination>(object? source)
        {
            if (source == null) return As<TDestination>(Run(null, null, typeof(object), typeof(TDestination), null));

            var sourceType = source.GetType();
            var entries = FastMapCache<TDestination>.Entries;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.SourceType != sourceType || !ReferenceEquals(entry.Configuration, _configuration)) continue;
                if (entry.Map != null && TryFast(entry.Map, source, out TDestination fast)) return fast;
                return As<TDestination>(Run(source, null, sourceType, typeof(TDestination), null));
            }

            _configuration.TryPopulateFastCacheForSource<TDestination>(sourceType);
            return As<TDestination>(Run(source, null, sourceType, typeof(TDestination), null));
        }

        public TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions> opts) =>
            As<TDestination>(Run(source, null, source?.GetType() ?? typeof(object), typeof(TDestination), opts));

        public TDestination Map<TSource, TDestination>(TSource source)
        {
            if (source != null && (typeof(TSource).IsValueType || source.GetType() == typeof(TSource)))
            {
                var entry = FastMapCache<TSource, TDestination>.Entry;
                if (entry != null && ReferenceEquals(entry.Configuration, _configuration))
                {
                    if (entry.Map != null && TryFast(entry.Map, source, out TDestination fast)) return fast;
                    return As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), null));
                }
            }
            if (source != null) _configuration.TryPopulateFastCache<TSource, TDestination>();
            return As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), null));
        }

        /// <summary>The fast path tracks no member path; on failure the call is re-run on the full path for a precise error.</summary>
        private static bool TryFast<TDestination>(CompiledMap map, object source, out TDestination result)
        {
            try
            {
                result = (TDestination)map(source, null, null);
                return true;
            }
            catch (MappingException)
            {
                result = default!;
                return false;
            }
        }

        public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions> opts) =>
            As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), opts));

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) =>
            As<TDestination>(Run(source, destination, typeof(TSource), typeof(TDestination), null));

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions> opts) =>
            As<TDestination>(Run(source, destination, typeof(TSource), typeof(TDestination), opts));

        public object? Map(object? source, Type sourceType, Type destinationType) => Run(source, null, sourceType, destinationType, null);

        public object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) =>
            Run(source, null, sourceType, destinationType, opts);

        public object? Map(object? source, object? destination, Type sourceType, Type destinationType) =>
            Run(source, destination, sourceType, destinationType, null);

        public object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) =>
            Run(source, destination, sourceType, destinationType, opts);

        public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source) => source.ProjectTo<TDestination>(_configuration);
    }
}
