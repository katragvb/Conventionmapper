using System;
using System.Linq;

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
            var context = new ResolutionContext(_configuration.Engine, options?.ServiceCtor ?? _serviceCtor, options?.RawItems);
            return _configuration.Engine.MapRoot(source, sourceType, destinationType, destination, context, options);
        }

        public TDestination Map<TDestination>(object? source) =>
            As<TDestination>(Run(source, null, source?.GetType() ?? typeof(object), typeof(TDestination), null));

        public TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions> opts) =>
            As<TDestination>(Run(source, null, source?.GetType() ?? typeof(object), typeof(TDestination), opts));

        public TDestination Map<TSource, TDestination>(TSource source) =>
            As<TDestination>(Run(source, null, typeof(TSource), typeof(TDestination), null));

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
