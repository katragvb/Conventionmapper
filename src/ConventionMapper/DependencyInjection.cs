using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConventionMapper;
using ConventionMapper.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// <c>services.AddConventionMapper(cfg =&gt; cfg.CreateMap&lt;Foo, FooDto&gt;(), typeof(Program));</c>
    /// Registers IConfigurationProvider/MapperConfiguration (singleton) and IMapper (transient), adds every Profile
    /// and [AutoMap] type found in the given assemblies, and registers implementations of IValueResolver,
    /// IMemberValueResolver, ITypeConverter, IValueConverter, ICondition, IPreCondition and IMappingAction as transient.
    /// Can be called multiple times; configuration accumulates.
    /// </summary>
    public static class ConventionMapperServiceCollectionExtensions
    {
        private static readonly Type[] OpenTypes =
        {
            typeof(IValueResolver<,,>),
            typeof(IMemberValueResolver<,,,>),
            typeof(ITypeConverter<,>),
            typeof(IValueConverter<,>),
            typeof(ICondition<,,>),
            typeof(IPreCondition<,>),
            typeof(IMappingAction<,>)
        };

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configAction) =>
            AddCore(services, Wrap(configAction), Array.Empty<Assembly>());

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configAction, params Assembly[] assemblies) =>
            AddCore(services, Wrap(configAction), assemblies);

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, Action<IMapperConfigurationExpression> configAction, params Type[] profileAssemblyMarkerTypes) =>
            AddCore(services, Wrap(configAction), profileAssemblyMarkerTypes.Select(t => t.Assembly));

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, Action<IServiceProvider, IMapperConfigurationExpression> configAction, params Assembly[] assemblies) =>
            AddCore(services, configAction ?? throw new ArgumentNullException(nameof(configAction)), assemblies);

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, params Assembly[] assemblies) =>
            AddCore(services, null, assemblies);

        [RequiresUnreferencedCode(TrimMessages.Message)]
        [RequiresDynamicCode(TrimMessages.Message)]
        public static IServiceCollection AddConventionMapper(this IServiceCollection services, params Type[] profileAssemblyMarkerTypes) =>
            AddCore(services, null, profileAssemblyMarkerTypes.Select(t => t.Assembly));

        private static Action<IServiceProvider, IMapperConfigurationExpression> Wrap(Action<IMapperConfigurationExpression> configAction)
        {
            if (configAction == null) throw new ArgumentNullException(nameof(configAction));
            return (_, cfg) => configAction(cfg);
        }

        private static IServiceCollection AddCore(IServiceCollection services, Action<IServiceProvider, IMapperConfigurationExpression>? configAction, IEnumerable<Assembly> assemblies)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            var registration = GetOrAddRegistration(services);
            if (configAction != null) registration.ConfigActions.Add(configAction);

            var list = (assemblies ?? Array.Empty<Assembly>()).Where(a => a != null).Distinct().ToList();
            foreach (var assembly in list)
            {
                if (registration.Assemblies.Contains(assembly)) continue;
                registration.Assemblies.Add(assembly);
                RegisterExtensionTypes(services, assembly);
            }
            return services;
        }

        private static MapperRegistration GetOrAddRegistration(IServiceCollection services)
        {
            foreach (var descriptor in services)
                if (descriptor.ServiceType == typeof(MapperRegistration) && descriptor.ImplementationInstance is MapperRegistration existing)
                    return existing;

            var registration = new MapperRegistration();
            services.AddSingleton(registration);
            services.AddSingleton(sp => new MapperConfiguration(cfg =>
            {
                cfg.ConstructServicesUsing(t => CreateService(sp, t));
                foreach (var action in registration.ConfigActions) action(sp, cfg);
                cfg.AddMaps(registration.Assemblies);
            }));
            services.AddSingleton<IConfigurationProvider>(sp => sp.GetRequiredService<MapperConfiguration>());
            services.AddTransient<IMapper>(sp => new Mapper(sp.GetRequiredService<IConfigurationProvider>(), t => CreateService(sp, t)));
            return registration;
        }

        private static object CreateService(IServiceProvider provider, Type type) =>
            provider.GetService(type) ?? ActivatorUtilities.CreateInstance(provider, type);

        private static void RegisterExtensionTypes(IServiceCollection services, Assembly assembly)
        {
            foreach (var type in ReflectionHelper.GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || !type.IsClass) continue;
                var implementsExtension = type.GetInterfaces().Any(i => i.IsGenericType && OpenTypes.Contains(i.GetGenericTypeDefinition()));
                if (implementsExtension) services.TryAddTransient(type);
            }
        }

        private sealed class MapperRegistration
        {
            public readonly List<Action<IServiceProvider, IMapperConfigurationExpression>> ConfigActions = new List<Action<IServiceProvider, IMapperConfigurationExpression>>();
            public readonly List<Assembly> Assemblies = new List<Assembly>();
        }
    }
}
