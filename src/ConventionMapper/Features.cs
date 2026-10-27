using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using ConventionMapper.Configuration;
using ConventionMapper.Internal;

namespace ConventionMapper
{
    public enum EnumMappingMode
    {
        /// <summary>Match enum members by name (default).</summary>
        ByName = 0,
        /// <summary>Match enum members by underlying numeric value.</summary>
        ByValue = 1
    }

    /// <summary>
    /// Options for <see cref="EnumMappingExtensions.ConvertUsingEnumMapping{TSource, TDestination}"/>. Add
    /// <see cref="EnumMappingExtensions.ReverseMap{TSource, TDestination}(IEnumMappingExpression{TSource, TDestination}, TSource?)"/>
    /// to also create the reverse map with the overrides inverted.
    /// </summary>
    public interface IEnumMappingExpression<TSource, TDestination>
        where TSource : struct, Enum
        where TDestination : struct, Enum
    {
        IEnumMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false);
        IEnumMappingExpression<TSource, TDestination> MapByValue();
        IEnumMappingExpression<TSource, TDestination> MapValue(TSource source, TDestination destination);
        /// <summary>Unmatched source values map to this value instead of failing validation.</summary>
        IEnumMappingExpression<TSource, TDestination> Fallback(TDestination destination);
    }

    public static class EnumMappingExtensions
    {
        /// <summary>
        /// Strict, validated enum mapping. Every source value must resolve to a destination value (by name, by value,
        /// an explicit MapValue or a Fallback); otherwise AssertConfigurationIsValid reports the missing values and
        /// mapping an unmatched value throws. Works with ProjectTo.
        /// </summary>
        public static void ConvertUsingEnumMapping<TSource, TDestination>(
            this IMappingExpression<TSource, TDestination> mapping,
            Action<IEnumMappingExpression<TSource, TDestination>>? options = null)
            where TSource : struct, Enum
            where TDestination : struct, Enum
        {
            if (!(mapping is MappingExpression<TSource, TDestination> typed))
                throw new ArgumentException("ConvertUsingEnumMapping must be called on a map created with CreateMap<TSource, TDestination>().", nameof(mapping));
            var expression = new EnumMappingExpression<TSource, TDestination>();
            options?.Invoke(expression);
            expression.Apply(typed.Config);

            // The reverse map gets the inverted mapping, whether ReverseMap() is called on the map before or after this,
            // or as ReverseMap() in the options.
            typed.Config.ConfigureReverse = reverse => expression.CreateReverse().Apply(reverse);
            var existing = typed.FindReverse();
            if (existing != null) typed.Config.ConfigureReverse(existing);
            else if (expression.Reverse) typed.ReverseMap();
        }

        /// <summary>
        /// Also creates the reverse map (<typeparamref name="TDestination"/> to <typeparamref name="TSource"/>) with the same
        /// matching (by name, case-insensitive or by value) and every <c>MapValue</c> override inverted:
        /// <code>
        /// cfg.CreateMap&lt;Color, ColorDto&gt;().ConvertUsingEnumMapping(o =&gt; o
        ///     .MapValue(Color.Purple, ColorDto.Violet)   // and ColorDto.Violet -&gt; Color.Purple
        ///     .ReverseMap());
        /// </code>
        /// A forward <c>Fallback</c> can't be inverted; pass <paramref name="fallback"/> for destination values with no match in
        /// <typeparamref name="TSource"/>. Two overrides onto the same destination value can't be inverted and fail validation.
        /// Calling <c>ReverseMap()</c> on the map itself (before or after <c>ConvertUsingEnumMapping</c>) inverts the mapping too.
        /// </summary>
        public static IEnumMappingExpression<TSource, TDestination> ReverseMap<TSource, TDestination>(
            this IEnumMappingExpression<TSource, TDestination> options, TSource? fallback = null)
            where TSource : struct, Enum
            where TDestination : struct, Enum
        {
            if (!(options is EnumMappingExpression<TSource, TDestination> expression))
                throw new ArgumentException("ReverseMap must be called on the options of ConvertUsingEnumMapping.", nameof(options));
            expression.Reverse = true;
            expression.ReverseFallback = fallback;
            return options;
        }
    }

    internal sealed class EnumMappingExpression<TSource, TDestination> : IEnumMappingExpression<TSource, TDestination>
        where TSource : struct, Enum
        where TDestination : struct, Enum
    {
        private readonly Dictionary<TSource, TDestination> _overrides = new Dictionary<TSource, TDestination>();
        private EnumMappingMode _mode = EnumMappingMode.ByName;
        private bool _ignoreCase;
        private bool _hasFallback;
        private TDestination _fallback;

        /// <summary>Overrides that can't be inverted: destination value -> the source values mapped onto it.</summary>
        private readonly List<string> _reverseErrors = new List<string>();

        /// <summary>ReverseMap() was called in the options.</summary>
        internal bool Reverse { get; set; }

        /// <summary>The reverse map's fallback, from ReverseMap(fallback).</summary>
        internal TSource? ReverseFallback { get; set; }

        public IEnumMappingExpression<TSource, TDestination> MapByName(bool ignoreCase = false)
        {
            _mode = EnumMappingMode.ByName;
            _ignoreCase = ignoreCase;
            return this;
        }

        public IEnumMappingExpression<TSource, TDestination> MapByValue()
        {
            _mode = EnumMappingMode.ByValue;
            return this;
        }

        public IEnumMappingExpression<TSource, TDestination> MapValue(TSource source, TDestination destination)
        {
            _overrides[source] = destination;
            return this;
        }

        public IEnumMappingExpression<TSource, TDestination> Fallback(TDestination destination)
        {
            _hasFallback = true;
            _fallback = destination;
            return this;
        }

        /// <summary>
        /// The same matching in the other direction, with each MapValue override inverted. A destination value that two
        /// overrides map onto can't be inverted: it is left out and reported by validation.
        /// </summary>
        internal EnumMappingExpression<TDestination, TSource> CreateReverse()
        {
            var reverse = new EnumMappingExpression<TDestination, TSource> { _mode = _mode, _ignoreCase = _ignoreCase };
            if (ReverseFallback.HasValue) reverse.Fallback(ReverseFallback.Value);
            foreach (var group in _overrides.GroupBy(o => o.Value))
            {
                var sources = group.Select(o => o.Key).Distinct().ToList();
                if (sources.Count == 1)
                {
                    reverse._overrides[group.Key] = sources[0];
                    continue;
                }
                reverse._ambiguous.Add(group.Key);
                reverse._reverseErrors.Add("enum value " + typeof(TDestination).Name + "." + group.Key + " is mapped from " +
                    string.Join(" and ", sources.Select(s => typeof(TSource).Name + "." + s)) +
                    " by MapValue, so the reverse map can't choose one. Configure CreateMap<" + typeof(TDestination).Name + ", " +
                    typeof(TSource).Name + ">() separately, or keep one MapValue for that value.");
            }
            return reverse;
        }

        /// <summary>Source values the reverse map can't map (see CreateReverse): never matched by name, value or fallback.</summary>
        private readonly HashSet<TSource> _ambiguous = new HashSet<TSource>();

        public void Apply(TypeMapConfiguration config)
        {
            var table = new Dictionary<TSource, TDestination>();
            var errors = new List<string>();
            var destinationNames = Enum.GetNames(typeof(TDestination));
            var destinationValues = (TDestination[])Enum.GetValues(typeof(TDestination));

            foreach (TSource value in Enum.GetValues(typeof(TSource)))
            {
                if (table.ContainsKey(value)) continue; // aliases share a value
                if (_ambiguous.Contains(value)) continue; // reported in _reverseErrors; mapping it throws
                if (_overrides.TryGetValue(value, out var mapped) || TryMatch(value, destinationNames, destinationValues, out mapped))
                {
                    table[value] = mapped;
                    continue;
                }
                if (_hasFallback)
                {
                    table[value] = _fallback;
                    continue;
                }
                errors.Add("enum value " + typeof(TSource).Name + "." + value + " has no matching " + typeof(TDestination).Name +
                           " value (matching " + (_mode == EnumMappingMode.ByName ? "by name" : "by value") + "). Add MapValue(...) or Fallback(...).");
            }

            var hasFallback = _hasFallback;
            var fallback = _fallback;
            var ambiguous = new HashSet<TSource>(_ambiguous);
            config.ValidationErrors.Clear();
            config.ValidationErrors.AddRange(_reverseErrors);
            config.ValidationErrors.AddRange(errors);
            config.TypeConverterType = null;
            config.TypeConverter = (source, destination, context) =>
            {
                var key = (TSource)source!;
                if (table.TryGetValue(key, out var result)) return result;
                if (hasFallback && !ambiguous.Contains(key)) return fallback;
                throw new MappingException("Enum value " + key + " of " + typeof(TSource).Name + " has no mapping to " + typeof(TDestination).Name + ".");
            };

            // Expression form so ProjectTo can translate it: src == A ? X : src == B ? Y : fallback
            var parameter = Expression.Parameter(typeof(TSource), "src");
            Expression body = Expression.Constant(hasFallback ? fallback : default(TDestination), typeof(TDestination));
            var entries = table.ToList();
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                body = Expression.Condition(
                    Expression.Equal(parameter, Expression.Constant(entries[i].Key, typeof(TSource))),
                    Expression.Constant(entries[i].Value, typeof(TDestination)),
                    body);
            }
            config.TypeConverterExpression = Expression.Lambda<Func<TSource, TDestination>>(body, parameter);
        }

        private bool TryMatch(TSource value, string[] destinationNames, TDestination[] destinationValues, out TDestination result)
        {
            if (_mode == EnumMappingMode.ByValue)
            {
                var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                foreach (var candidate in destinationValues)
                {
                    if (Convert.ToInt64(candidate, CultureInfo.InvariantCulture) != number) continue;
                    result = candidate;
                    return true;
                }
                result = default;
                return false;
            }

            var name = Enum.GetName(typeof(TSource), value);
            if (name != null)
            {
                var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (var destinationName in destinationNames)
                {
                    if (!string.Equals(destinationName, name, comparison)) continue;
                    result = (TDestination)Enum.Parse(typeof(TDestination), destinationName);
                    return true;
                }
            }
            result = default;
            return false;
        }
    }

    public static class MapperExtensions
    {
        /// <summary>
        /// Applies only the non-null values of <paramref name="source"/> to <paramref name="destination"/>
        /// (HTTP PATCH semantics). Equivalent to Map(source, destination, opt =&gt; opt.IgnoreNullSourceValues()).
        /// </summary>
        public static TDestination Patch<TSource, TDestination>(this IMapper mapper, TSource source, TDestination destination)
        {
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            return mapper.Map(source, destination, opt => opt.IgnoreNullSourceValues());
        }
    }
}
