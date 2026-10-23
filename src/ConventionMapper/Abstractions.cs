using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace ConventionMapper
{
    /// <summary>Which side of a map is checked by <c>AssertConfigurationIsValid</c>.</summary>
    public enum MemberList
    {
        /// <summary>Every writable destination member must be mapped or ignored (default).</summary>
        Destination = 0,
        /// <summary>Every readable source member must be used or marked DoNotValidate.</summary>
        Source = 1,
        /// <summary>No member validation.</summary>
        None = 2
    }

    // ---------------------------------------------------------------------------------
    // Extension points (all auto-registered by AddConventionMapper when assemblies are scanned)
    // ---------------------------------------------------------------------------------

    /// <summary>Custom value resolver for a destination member.</summary>
    public interface IValueResolver<in TSource, in TDestination, TDestMember>
    {
        TDestMember Resolve(TSource source, TDestination destination, TDestMember destMember, ResolutionContext context);
    }

    /// <summary>Custom value resolver that receives a pre-selected source member.</summary>
    public interface IMemberValueResolver<in TSource, in TDestination, in TSourceMember, TDestMember>
    {
        TDestMember Resolve(TSource source, TDestination destination, TSourceMember sourceMember, TDestMember destMember, ResolutionContext context);
    }

    /// <summary>Takes complete control over converting one type to another.</summary>
    public interface ITypeConverter<in TSource, TDestination>
    {
        TDestination Convert(TSource source, TDestination destination, ResolutionContext context);
    }

    /// <summary>Converts a single member value.</summary>
    public interface IValueConverter<in TSourceMember, out TDestinationMember>
    {
        TDestinationMember Convert(TSourceMember sourceMember, ResolutionContext context);
    }

    /// <summary>Reusable before/after map action.</summary>
    public interface IMappingAction<in TSource, in TDestination>
    {
        void Process(TSource source, TDestination destination, ResolutionContext context);
    }

    /// <summary>Reusable member condition, evaluated after the source value has been resolved.</summary>
    public interface ICondition<in TSource, in TDestination, in TMember>
    {
        bool Evaluate(TSource source, TDestination destination, TMember sourceMember, TMember destinationMember, ResolutionContext context);
    }

    /// <summary>Reusable member pre-condition, evaluated before the source value is resolved.</summary>
    public interface IPreCondition<in TSource, in TDestination>
    {
        bool Evaluate(TSource source, TDestination destination, ResolutionContext context);
    }

    // ---------------------------------------------------------------------------------
    // Attribute mapping (discovered by AddMaps / AddConventionMapper assembly scanning)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Declares a map from <see cref="SourceType"/> to the decorated type:
    /// <c>[MappedFrom(typeof(Person), ReverseMap = true)] public class PersonDto { ... }</c>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class MappedFromAttribute : Attribute
    {
        public MappedFromAttribute(Type sourceType) { SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType)); }
        public Type SourceType { get; }
        public bool ReverseMap { get; set; }
        public int MaxDepth { get; set; }
        public bool PreserveReferences { get; set; }
        public MemberList MemberList { get; set; } = MemberList.Destination;
    }

    /// <summary>Kept for compatibility with 1.x; use <see cref="MappedFromAttribute"/>.</summary>
    [Obsolete("Use [MappedFrom] instead.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class AutoMapAttribute : Attribute
    {
        public AutoMapAttribute(Type sourceType) { SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType)); }
        public Type SourceType { get; }
        public bool ReverseMap { get; set; }
        public int MaxDepth { get; set; }
        public bool PreserveReferences { get; set; }
        public MemberList MemberList { get; set; } = MemberList.Destination;
    }

    /// <summary>Ignores the decorated destination member.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class IgnoreMapAttribute : Attribute { }

    /// <summary>Maps the decorated destination member from the named source member (dotted paths allowed).</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class SourceMemberAttribute : Attribute
    {
        public SourceMemberAttribute(string name) { Name = name ?? throw new ArgumentNullException(nameof(name)); }
        public string Name { get; }
    }

    // ---------------------------------------------------------------------------------
    // Naming conventions
    // ---------------------------------------------------------------------------------

    /// <summary>Splits member names into words, used for matching and flattening.</summary>
    public interface INamingConvention
    {
        string[] Split(string input);
        string SeparatorCharacter { get; }
    }

    /// <summary>PascalCase / camelCase (default): <c>CustomerName</c> → Customer, Name.</summary>
    public sealed class PascalCaseNamingConvention : INamingConvention
    {
        public static readonly PascalCaseNamingConvention Instance = new PascalCaseNamingConvention();
        private static readonly Regex Splitter = new Regex(@"\p{Lu}+(?=$|\p{Lu}[\p{Ll}0-9])|\p{Lu}?[\p{Ll}0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public string SeparatorCharacter => string.Empty;

        public string[] Split(string input)
        {
            if (string.IsNullOrEmpty(input)) return new[] { input ?? string.Empty };
            var matches = Splitter.Matches(input);
            if (matches.Count == 0) return new[] { input };
            var result = new string[matches.Count];
            for (var i = 0; i < matches.Count; i++) result[i] = matches[i].Value;
            return result;
        }
    }

    /// <summary>lower_underscore: <c>customer_name</c> → customer, name.</summary>
    public sealed class LowerUnderscoreNamingConvention : INamingConvention
    {
        public static readonly LowerUnderscoreNamingConvention Instance = new LowerUnderscoreNamingConvention();
        public string SeparatorCharacter => "_";

        public string[] Split(string input)
        {
            var parts = (input ?? string.Empty).Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? new[] { input ?? string.Empty } : parts;
        }
    }

    /// <summary>Whole name is a single word (disables flattening). Matching remains case-insensitive.</summary>
    public sealed class ExactMatchNamingConvention : INamingConvention
    {
        public static readonly ExactMatchNamingConvention Instance = new ExactMatchNamingConvention();
        public string SeparatorCharacter => string.Empty;
        public string[] Split(string input) => new[] { input ?? string.Empty };
    }

    // ---------------------------------------------------------------------------------
    // Value transformers
    // ---------------------------------------------------------------------------------

    /// <summary>A transformation applied to every mapped value of <see cref="ValueType"/> (null values are skipped).</summary>
    public sealed class ValueTransformerConfiguration
    {
        internal ValueTransformerConfiguration(Type valueType, LambdaExpression expression, Func<object?, object?> transform)
        {
            ValueType = valueType;
            TransformerExpression = expression;
            Transform = transform;
        }

        public Type ValueType { get; }
        public LambdaExpression TransformerExpression { get; }
        internal Func<object?, object?> Transform { get; }

        public static ValueTransformerConfiguration Create<TValue>(Expression<Func<TValue, TValue>> transformer)
        {
            if (transformer == null) throw new ArgumentNullException(nameof(transformer));
            var compiled = transformer.Compile();
            return new ValueTransformerConfiguration(typeof(TValue), transformer, v => v is TValue typed ? compiled(typed) : v);
        }
    }

    public static class ValueTransformerConfigurationExtensions
    {
        /// <summary><c>cfg.ValueTransformers.Add&lt;string&gt;(s =&gt; s.Trim());</c></summary>
        public static void Add<TValue>(this IList<ValueTransformerConfiguration> transformers, Expression<Func<TValue, TValue>> transformer)
        {
            if (transformers == null) throw new ArgumentNullException(nameof(transformers));
            transformers.Add(ValueTransformerConfiguration.Create(transformer));
        }
    }

    /// <summary>A source/destination type pair.</summary>
    public readonly struct TypePair : IEquatable<TypePair>
    {
        public TypePair(Type sourceType, Type destinationType)
        {
            SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
            DestinationType = destinationType ?? throw new ArgumentNullException(nameof(destinationType));
        }

        public Type SourceType { get; }
        public Type DestinationType { get; }

        public bool Equals(TypePair other) => SourceType == other.SourceType && DestinationType == other.DestinationType;
        public override bool Equals(object? obj) => obj is TypePair other && Equals(other);
        public override int GetHashCode() => unchecked((SourceType.GetHashCode() * 397) ^ DestinationType.GetHashCode());
        public override string ToString() => SourceType.Name + " -> " + DestinationType.Name;
    }
}
