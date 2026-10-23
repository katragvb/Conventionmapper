using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ConventionMapper.Internal
{
    internal sealed class MemberConfig
    {
        public MemberConfig(string name) { Name = name; }

        public string Name;
        public bool Ignored;
        public ValueSource? Source;
        public Func<object?, object?, ResolutionContext, bool>? PreCondition;
        public Func<object?, object?, object?, object?, ResolutionContext, bool>? Condition;
        public bool HasNullSubstitute;
        public object? NullSubstitute;
        public bool UseDestinationValue;
        public int? MappingOrder;
        public bool ExplicitExpansion;
        public List<ValueTransformerConfiguration> Transformers = new List<ValueTransformerConfiguration>();

        public bool IsExplicit => Ignored || Source != null;

        public MemberConfig Clone()
        {
            var clone = (MemberConfig)MemberwiseClone();
            clone.Transformers = new List<ValueTransformerConfiguration>(Transformers);
            return clone;
        }
    }

    internal sealed class CtorParamConfig
    {
        public CtorParamConfig(string name) { Name = name; }
        public string Name;
        public ValueSource? Source;
    }

    internal sealed class PathConfig
    {
        public PathConfig(MemberInfo[] path) { Path = path; }
        public MemberInfo[] Path;
        public ValueSource? Source;
        public bool Ignored;
        public Func<object?, object?, bool>? Condition;
    }

    internal sealed class TypeMapConfiguration
    {
        public TypeMapConfiguration(Type sourceType, Type destinationType, MemberList memberList)
        {
            SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
            DestinationType = destinationType ?? throw new ArgumentNullException(nameof(destinationType));
            MemberList = memberList;
        }

        public Type SourceType;
        public Type DestinationType;
        public MemberList MemberList;

        public Dictionary<string, MemberConfig> Members = new Dictionary<string, MemberConfig>(StringComparer.Ordinal);
        public List<PathConfig> Paths = new List<PathConfig>();
        public Dictionary<string, CtorParamConfig> CtorParams = new Dictionary<string, CtorParamConfig>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SourceMembersNotValidated = new HashSet<string>(StringComparer.Ordinal);
        public List<Action<object, object, ResolutionContext>> BeforeMaps = new List<Action<object, object, ResolutionContext>>();
        public List<Action<object, object, ResolutionContext>> AfterMaps = new List<Action<object, object, ResolutionContext>>();
        public List<TypePair> IncludedDerived = new List<TypePair>();
        public List<TypePair> IncludedBase = new List<TypePair>();
        public List<LambdaExpression> IncludedMembers = new List<LambdaExpression>();
        public List<ValueTransformerConfiguration> ValueTransformers = new List<ValueTransformerConfiguration>();

        public Func<object, ResolutionContext, object>? CustomCtor;
        public LambdaExpression? CustomCtorExpression;
        public Func<object?, object?, ResolutionContext, object?>? TypeConverter;
        public LambdaExpression? TypeConverterExpression;
        public Type? TypeConverterType;

        public int MaxDepth;
        public bool PreserveReferences;
        public bool DisableCtorValidation;
        public bool IgnoreInaccessibleSetters;
        public TypeMapConfiguration? ReverseOf;
        public Func<object, object, bool>? EqualityComparison;
        public bool IgnoreNullSourceValues;
        public List<string> ValidationErrors = new List<string>();

        public MemberConfig GetOrAddMember(string name)
        {
            if (!Members.TryGetValue(name, out var config))
            {
                config = new MemberConfig(name);
                Members[name] = config;
            }
            return config;
        }

        public CtorParamConfig GetOrAddCtorParam(string name)
        {
            if (!CtorParams.TryGetValue(name, out var config))
            {
                config = new CtorParamConfig(name);
                CtorParams[name] = config;
            }
            return config;
        }

        public TypeMapConfiguration Clone()
        {
            var c = (TypeMapConfiguration)MemberwiseClone();
            c.Members = Members.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal);
            c.Paths = new List<PathConfig>(Paths);
            c.CtorParams = new Dictionary<string, CtorParamConfig>(CtorParams, StringComparer.OrdinalIgnoreCase);
            c.SourceMembersNotValidated = new HashSet<string>(SourceMembersNotValidated, StringComparer.Ordinal);
            c.BeforeMaps = new List<Action<object, object, ResolutionContext>>(BeforeMaps);
            c.AfterMaps = new List<Action<object, object, ResolutionContext>>(AfterMaps);
            c.IncludedDerived = new List<TypePair>(IncludedDerived);
            c.IncludedBase = new List<TypePair>(IncludedBase);
            c.IncludedMembers = new List<LambdaExpression>(IncludedMembers);
            c.ValueTransformers = new List<ValueTransformerConfiguration>(ValueTransformers);
            c.ValidationErrors = new List<string>(ValidationErrors);
            return c;
        }

        /// <summary>Creates the configuration for a closed generic pair from an open generic map.</summary>
        public TypeMapConfiguration CloseGeneric(Type closedSource, Type closedDestination)
        {
            var c = Clone();
            c.SourceType = closedSource;
            c.DestinationType = closedDestination;
            c.ReverseOf = null;
            if (TypeConverterType != null && TypeConverterType.IsGenericTypeDefinition)
            {
                var arity = TypeConverterType.GetGenericArguments().Length;
                var sourceArgs = closedSource.IsGenericType ? closedSource.GetGenericArguments() : Type.EmptyTypes;
                var destArgs = closedDestination.IsGenericType ? closedDestination.GetGenericArguments() : Type.EmptyTypes;
                Type[] args;
                if (sourceArgs.Length + destArgs.Length == arity) args = sourceArgs.Concat(destArgs).ToArray();
                else if (sourceArgs.Length == arity) args = sourceArgs;
                else if (destArgs.Length == arity) args = destArgs;
                else throw new MapperConfigurationException("Cannot close open generic type converter " + TypeConverterType + " for " + closedSource + " -> " + closedDestination + ".");
                c.TypeConverter = CreateReflectionConverter(TypeConverterType.MakeGenericType(args));
            }
            return c;
        }

        public static Func<object?, object?, ResolutionContext, object?> CreateReflectionConverter(Type converterType)
        {
            var iface = ReflectionHelper.FindGenericInterface(converterType, typeof(ITypeConverter<,>))
                        ?? throw new ArgumentException(converterType + " does not implement ITypeConverter<,>.", nameof(converterType));
            var method = iface.GetMethod("Convert")!;
            return (source, destination, context) =>
            {
                var instance = context.CreateService(converterType);
                try
                {
                    return method.Invoke(instance, new[] { source, destination, context });
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            };
        }
    }
}
