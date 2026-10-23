using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ConventionMapper.Internal
{
    internal static class CastHelper
    {
        public static T Cast<T>(object? value) => value is T typed ? typed : default!;
    }

    internal static class ReflectionHelper
    {
        private static readonly HashSet<string> ExcludedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetType", "GetHashCode", "ToString", "Equals", "MemberwiseClone", "GetEnumerator", "Clone", "GetTypeCode", "Deconstruct"
        };

        /// <summary>"List&lt;OrderLine&gt;" instead of "List`1".</summary>
        public static string FriendlyName(Type type)
        {
            if (type.IsArray) return FriendlyName(type.GetElementType()!) + "[]";
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return FriendlyName(underlying) + "?";
            if (!type.IsGenericType) return type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        public static Type GetMemberType(MemberInfo member)
        {
            if (member is PropertyInfo p) return p.PropertyType;
            if (member is FieldInfo f) return f.FieldType;
            if (member is MethodInfo m) return m.ReturnType;
            throw new ArgumentOutOfRangeException(nameof(member), "Unsupported member " + member);
        }

        public static bool IsCollectionType(Type type) => type != typeof(string) && (type.IsArray || typeof(IEnumerable).IsAssignableFrom(type));

        public static Type GetElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType()!;
            var enumerable = FindGenericInterface(type, typeof(IEnumerable<>));
            return enumerable != null ? enumerable.GetGenericArguments()[0] : typeof(object);
        }

        public static Type? FindGenericInterface(Type type, Type openGeneric)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == openGeneric) return type;
            foreach (var iface in type.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openGeneric) return iface;
            return null;
        }

        public static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
        {
            var dictionary = FindGenericInterface(type, typeof(IDictionary<,>)) ?? FindGenericInterface(type, typeof(IReadOnlyDictionary<,>));
            if (dictionary != null)
            {
                var args = dictionary.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
            keyType = null!;
            valueType = null!;
            return false;
        }

        public static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
            t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

        public static bool IsNumeric(Type t) => IsIntegral(t) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);

        public static IEnumerable<PropertyInfo> GetAllProperties(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (!type.IsInterface) return type.GetProperties(flags);
            return new[] { type }.Concat(type.GetInterfaces()).SelectMany(i => i.GetProperties(flags));
        }

        public static IReadOnlyList<MemberInfo> GetSourceMembers(Type type, Func<PropertyInfo, bool> shouldMapProperty, Func<FieldInfo, bool> shouldMapField, Func<MethodInfo, bool> shouldMapMethod)
        {
            var result = new List<MemberInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in GetAllProperties(type))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetGetMethod(false) == null) continue;
                if (!shouldMapProperty(p)) continue;
                if (names.Add(p.Name)) result.Add(p);
            }
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (shouldMapField(f) && names.Add(f.Name)) result.Add(f);
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName || m.IsGenericMethodDefinition || m.ReturnType == typeof(void) || m.GetParameters().Length != 0) continue;
                if (m.DeclaringType == typeof(object) || ExcludedMethods.Contains(m.Name) || m.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                if (!shouldMapMethod(m)) continue;
                if (names.Add(m.Name)) result.Add(m);
            }
            return result;
        }

        public static IReadOnlyList<DestinationMember> GetDestinationMembers(Type type, Func<PropertyInfo, bool> shouldMapProperty, Func<FieldInfo, bool> shouldMapField)
        {
            var result = new List<DestinationMember>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in GetAllProperties(type))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (!shouldMapProperty(p)) continue;
                if (!names.Add(p.Name)) continue;
                result.Add(new DestinationMember(GetDeclaredProperty(p)));
            }
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsLiteral || !shouldMapField(f)) continue;
                if (names.Add(f.Name)) result.Add(new DestinationMember(f));
            }
            return result;
        }

        public static IEnumerable<string> GetDestinationMemberNames(Type type)
        {
            foreach (var p in GetAllProperties(type))
                if (p.GetIndexParameters().Length == 0) yield return p.Name;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!f.IsInitOnly && !f.IsLiteral) yield return f.Name;
        }

        /// <summary>Private setters declared on a base class are only visible on the declaring type's PropertyInfo.</summary>
        private static PropertyInfo GetDeclaredProperty(PropertyInfo property)
        {
            var declaring = property.DeclaringType;
            if (declaring == null || declaring == property.ReflectedType) return property;
            try
            {
                return declaring.GetProperty(property.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) ?? property;
            }
            catch (AmbiguousMatchException)
            {
                return property;
            }
        }

        /// <summary>Resolves "Customer.Address.City" (case-insensitive) against a type.</summary>
        public static MemberInfo[]? ResolveMemberPath(Type type, string path)
        {
            var segments = path.Split('.');
            var result = new List<MemberInfo>();
            var current = type;
            foreach (var segment in segments)
            {
                var member = GetSourceMembers(current, _ => true, _ => true, _ => true)
                    .FirstOrDefault(m => string.Equals(m.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (member == null) return null;
                result.Add(member);
                current = GetMemberType(member);
            }
            return result.ToArray();
        }

        public static bool HasMember(Type type, string name) =>
            GetDestinationMemberNames(type).Any(n => string.Equals(n, name, StringComparison.Ordinal));

        public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
        }
    }

    /// <summary>A writable/readable destination member with compiled accessors.</summary>
    internal sealed class DestinationMember
    {
        public DestinationMember(MemberInfo member)
        {
            Member = member;
            Name = member.Name;
            Type = ReflectionHelper.GetMemberType(member);
            if (member is PropertyInfo p)
            {
                var getter = p.GetGetMethod(true);
                var setter = p.GetSetMethod(true);
                CanRead = getter != null;
                CanWrite = setter != null;
                HasPublicSetter = setter != null && setter.IsPublic;
            }
            else
            {
                CanRead = true;
                CanWrite = !((FieldInfo)member).IsInitOnly;
                HasPublicSetter = CanWrite;
            }
            Getter = CanRead ? AccessorFactory.CreateGetter(member) : null;
            Setter = CanWrite ? AccessorFactory.CreateSetter(member) : null;
        }

        public MemberInfo Member { get; }
        public string Name { get; }
        public Type Type { get; }
        public bool CanRead { get; }
        public bool CanWrite { get; }
        public bool HasPublicSetter { get; }
        public Func<object, object?>? Getter { get; }
        public Action<object, object?>? Setter { get; }
    }

    internal static class AccessorFactory
    {
        private static readonly ConcurrentDictionary<MemberInfo, Func<object, object?>> Getters = new ConcurrentDictionary<MemberInfo, Func<object, object?>>();
        private static readonly ConcurrentDictionary<MemberInfo, Action<object, object?>> Setters = new ConcurrentDictionary<MemberInfo, Action<object, object?>>();

        public static Func<object, object?> CreateGetter(MemberInfo member) => Getters.GetOrAdd(member, BuildGetter);

        public static Action<object, object?> CreateSetter(MemberInfo member) => Setters.GetOrAdd(member, BuildSetter);

        private static Func<object, object?> BuildGetter(MemberInfo member)
        {
            try
            {
                var declaring = member.DeclaringType!;
                var obj = Expression.Parameter(typeof(object), "o");
                Expression typed = declaring.IsValueType ? Expression.Unbox(obj, declaring) : Expression.Convert(obj, declaring);
                Expression access;
                if (member is PropertyInfo p) access = Expression.Call(typed, p.GetGetMethod(true)!);
                else if (member is FieldInfo f) access = Expression.Field(typed, f);
                else access = Expression.Call(typed, (MethodInfo)member);
                var body = Expression.Convert(access, typeof(object));
                return Expression.Lambda<Func<object, object?>>(body, obj).Compile();
            }
            catch (Exception)
            {
                if (member is PropertyInfo p) return o => p.GetValue(o, null);
                if (member is FieldInfo f) return o => f.GetValue(o);
                var m = (MethodInfo)member;
                return o => m.Invoke(o, null);
            }
        }

        private static Action<object, object?> BuildSetter(MemberInfo member)
        {
            var declaring = member.DeclaringType!;
            if (!declaring.IsValueType)
            {
                try
                {
                    var obj = Expression.Parameter(typeof(object), "o");
                    var value = Expression.Parameter(typeof(object), "v");
                    var memberType = ReflectionHelper.GetMemberType(member);
                    var typedObj = Expression.Convert(obj, declaring);
                    Expression typedValue = memberType.IsValueType
                        ? Expression.Condition(Expression.Equal(value, Expression.Constant(null)), Expression.Default(memberType), Expression.Convert(value, memberType))
                        : (Expression)Expression.Convert(value, memberType);
                    Expression body = member is PropertyInfo p
                        ? Expression.Call(typedObj, p.GetSetMethod(true)!, typedValue)
                        : Expression.Assign(Expression.Field(typedObj, (FieldInfo)member), typedValue);
                    return Expression.Lambda<Action<object, object?>>(body, obj, value).Compile();
                }
                catch (Exception)
                {
                    // fall through to reflection
                }
            }
            // Reflection mutates boxed structs in place, which compiled delegates cannot.
            if (member is PropertyInfo prop) return (o, v) => prop.SetValue(o, v, null);
            var field = (FieldInfo)member;
            return (o, v) => field.SetValue(o, v);
        }

        public static Func<object>? CreateFactory(Type type)
        {
            if (type.IsValueType) return () => Activator.CreateInstance(type)!;
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) return null;
            var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null) return null;
            try
            {
                return Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object))).Compile();
            }
            catch (Exception)
            {
                return () => ctor.Invoke(null);
            }
        }
    }

    internal static class ExpressionHelper
    {
        public static Expression StripConvert(Expression expression)
        {
            while (expression.NodeType == ExpressionType.Convert || expression.NodeType == ExpressionType.ConvertChecked)
                expression = ((UnaryExpression)expression).Operand;
            return expression;
        }

        /// <summary>For <c>x =&gt; x.A.B.GetC()</c> returns [A, B, GetC]; for <c>x =&gt; x</c> returns []; otherwise null.</summary>
        public static MemberInfo[]? ExtractChain(LambdaExpression lambda)
        {
            var parameter = lambda.Parameters[0];
            var members = new List<MemberInfo>();
            var current = StripConvert(lambda.Body);
            while (true)
            {
                if (current == parameter) break;
                if (current is MemberExpression me && me.Expression != null)
                {
                    members.Add(me.Member);
                    current = me.Expression;
                    continue;
                }
                if (current is MethodCallExpression mc && mc.Object != null && mc.Arguments.Count == 0)
                {
                    members.Add(mc.Method);
                    current = mc.Object;
                    continue;
                }
                return null;
            }
            members.Reverse();
            return members.ToArray();
        }

        public static MemberInfo GetTopLevelMember(LambdaExpression lambda)
        {
            var chain = ExtractChain(lambda);
            if (chain == null || chain.Length != 1 || chain[0] is MethodInfo)
                throw new ArgumentException("Expression '" + lambda + "' must resolve to a top-level member (e.g. d => d.Name). Use ForPath for nested members.", nameof(lambda));
            return chain[0];
        }

        public static Expression ReplaceParameter(LambdaExpression lambda, Expression replacement)
        {
            var parameter = lambda.Parameters[0];
            if (!parameter.Type.IsAssignableFrom(replacement.Type)) replacement = Expression.Convert(replacement, parameter.Type);
            return new ParameterReplacer(parameter, replacement).Visit(lambda.Body)!;
        }

        public static IEnumerable<string> CollectRootMembers(LambdaExpression lambda)
        {
            var collector = new RootMemberCollector(lambda.Parameters[0]);
            collector.Visit(lambda.Body);
            return collector.Names;
        }

        private sealed class ParameterReplacer : ExpressionVisitor
        {
            private readonly ParameterExpression _from;
            private readonly Expression _to;

            public ParameterReplacer(ParameterExpression from, Expression to)
            {
                _from = from;
                _to = to;
            }

            protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
        }

        private sealed class RootMemberCollector : ExpressionVisitor
        {
            private readonly ParameterExpression _parameter;
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);

            public RootMemberCollector(ParameterExpression parameter) { _parameter = parameter; }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Expression == _parameter) Names.Add(node.Member.Name);
                return base.VisitMember(node);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Object == _parameter) Names.Add(node.Method.Name);
                return base.VisitMethodCall(node);
            }
        }
    }
}
