using System;

namespace ConventionMapper
{
    public static class ContextPoolingExtensions
    {
        /// <summary>
        /// Allocate a new <see cref="ResolutionContext"/> for every Map call instead of reusing one per thread.
        /// Only needed when a resolver, converter or action keeps a reference to the context (or its Items) after Map returns.
        /// </summary>
        public static IMapperConfigurationExpression DisableContextPooling(this IMapperConfigurationExpression configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!(configuration is MapperConfigurationExpression expression))
                throw new ArgumentException("Context pooling can only be configured on a MapperConfigurationExpression.", nameof(configuration));
            expression.UseContextPooling = false;
            return configuration;
        }
    }
}
