using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ConventionMapper.Internal
{
    /// <summary>Produces the raw (not yet converted) value for a destination member or ctor parameter.</summary>
    internal abstract class ValueSource
    {
        public abstract Type ValueType { get; }

        public abstract object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context);

        /// <summary>Expression for ProjectTo, or null when this source cannot be translated.</summary>
        public virtual Expression? BuildProjection(Expression source) => null;

        /// <summary>Top-level source member names this source reads (for MemberList.Source validation).</summary>
        public virtual IEnumerable<string> ReferencedSourceMembers => Array.Empty<string>();

        /// <summary>Description used by Explain().</summary>
        public virtual string Describe() => "custom function / resolver";
    }

    /// <summary>Null-safe member chain (convention matches, flattening, simple MapFrom expressions).</summary>
    internal sealed class ChainSource : ValueSource
    {
        private readonly Func<object, object?>[] _getters;
        private readonly Type _valueType;

        public ChainSource(MemberInfo[] members, Type rootType)
        {
            Members = members;
            _getters = members.Select(AccessorFactory.CreateGetter).ToArray();
            _valueType = members.Length == 0 ? rootType : ReflectionHelper.GetMemberType(members[members.Length - 1]);
        }

        public MemberInfo[] Members { get; }
        public override Type ValueType => _valueType;

        public override object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context)
        {
            object? current = source;
            for (var i = 0; i < _getters.Length; i++)
            {
                if (current == null) return null;
                current = _getters[i](current);
            }
            return current;
        }

        public override Expression? BuildProjection(Expression source)
        {
            var current = source;
            foreach (var member in Members)
            {
                var declaring = member.DeclaringType;
                if (declaring != null && (declaring.IsInterface ? current.Type != declaring : !declaring.IsAssignableFrom(current.Type)))
                    current = Expression.Convert(current, declaring);
                current = member is MethodInfo method ? Expression.Call(current, method) : Expression.MakeMemberAccess(current, member);
            }
            return current;
        }

        public override IEnumerable<string> ReferencedSourceMembers => Members.Length > 0 ? new[] { Members[0].Name } : Array.Empty<string>();

        public override string Describe() =>
            Members.Length == 0 ? "(source object)" : string.Join(".", Members.Select(m => m is MethodInfo ? m.Name + "()" : m.Name));
    }

    /// <summary>Arbitrary MapFrom expression; null references inside the expression yield null.</summary>
    internal sealed class ExpressionSource : ValueSource
    {
        private readonly Func<object, object?> _func;
        private readonly Type _valueType;

        public ExpressionSource(LambdaExpression lambda, Type valueType, Func<object, object?> func)
        {
            Lambda = lambda;
            _valueType = valueType;
            _func = func;
        }

        public LambdaExpression Lambda { get; }
        public override Type ValueType => _valueType;

        public override object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context) => _func(source);

        public override Expression? BuildProjection(Expression source) => ExpressionHelper.ReplaceParameter(Lambda, source);

        public override IEnumerable<string> ReferencedSourceMembers => ExpressionHelper.CollectRootMembers(Lambda);

        public override string Describe() => Lambda.ToString();
    }

    /// <summary>Delegate-based resolution (MapFrom(func), value resolvers, member value resolvers).</summary>
    internal sealed class DelegateSource : ValueSource
    {
        private readonly Func<object, object?, object?, ResolutionContext, object?> _func;
        private readonly Type _valueType;

        public DelegateSource(Type valueType, Func<object, object?, object?, ResolutionContext, object?> func)
        {
            _valueType = valueType;
            _func = func;
        }

        public override Type ValueType => _valueType;

        public override object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context) =>
            _func(source, destination, destinationMember, context);
    }

    /// <summary>Wraps another source and runs an IValueConverter on its result.</summary>
    internal sealed class ConverterSource : ValueSource
    {
        private readonly Func<object?, ResolutionContext, object?> _convert;
        private readonly Type _valueType;

        public ConverterSource(ValueSource inner, Type valueType, Func<object?, ResolutionContext, object?> convert)
        {
            Inner = inner;
            _valueType = valueType;
            _convert = convert;
        }

        public ValueSource Inner { get; }
        public override Type ValueType => _valueType;

        public ConverterSource WithInner(ValueSource inner) => new ConverterSource(inner, _valueType, _convert);

        public override object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context) =>
            _convert(Inner.Resolve(source, destination, destinationMember, context), context);

        public override IEnumerable<string> ReferencedSourceMembers => Inner.ReferencedSourceMembers;

        public override string Describe() => Inner.Describe() + " via value converter";
    }

    /// <summary>A member path by name ("Customer.Name"), resolved when the map is sealed.</summary>
    internal sealed class PathStringSource : ValueSource
    {
        public PathStringSource(string path, bool useConvention)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Source member path cannot be empty.", nameof(path));
            Path = path;
            UseConvention = useConvention;
        }

        public string Path { get; }
        public bool UseConvention { get; }
        public override Type ValueType => typeof(object);

        public override object? Resolve(object source, object? destination, object? destinationMember, ResolutionContext context) =>
            throw new InvalidOperationException("Unresolved source member path '" + Path + "'.");
    }

    internal static class ValueSources
    {
        public static ValueSource FromExpression<TSource, TMember>(Expression<Func<TSource, TMember>> expression)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            var chain = ExpressionHelper.ExtractChain(expression);
            if (chain != null) return new ChainSource(chain, typeof(TSource));
            var compiled = expression.Compile();
            return new ExpressionSource(expression, typeof(TMember), source =>
            {
                try
                {
                    return compiled(CastHelper.Cast<TSource>(source));
                }
                catch (NullReferenceException)
                {
                    return null;
                }
            });
        }
    }
}
