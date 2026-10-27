using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ConventionMapper.Testing
{
    /// <summary>
    /// Builds test objects fluently from <see cref="SampleData"/>: every member is filled with distinct values, then the
    /// members you name are set. Member paths can be nested (<c>r =&gt; r.Address.City</c>; missing objects on the way are
    /// created), and private or init-only setters work, so entities that guard their state can be built too.
    /// <code>
    /// var referral = SampleData.Builder&lt;Referral&gt;()
    ///     .With(r =&gt; r.Status, "A")
    ///     .With(r =&gt; r.Address.City, "Leeds")
    ///     .Without(r =&gt; r.ClosedOn)
    ///     .Build();
    ///
    /// var referrals = SampleData.Builder&lt;Referral&gt;().With(r =&gt; r.Number, i =&gt; 1000 + i).BuildMany(3);
    /// </code>
    /// </summary>
    public sealed class Builder<T>
    {
        private readonly SampleDataOptions? _options;
        private readonly List<Action<T, int>> _steps = new List<Action<T, int>>();
        private bool _blank;

        internal Builder(SampleDataOptions? options)
        {
            _options = options;
        }

        /// <summary>Start from a new, empty <typeparamref name="T"/> (its constructor's values) instead of a filled one.</summary>
        public Builder<T> Blank()
        {
            _blank = true;
            return this;
        }

        /// <summary>Sets the member (or nested member path) to <paramref name="value"/>.</summary>
        public Builder<T> With<TMember>(Expression<Func<T, TMember>> member, TMember value)
        {
            var path = Path(member);
            _steps.Add((target, _) => Set(target!, path, value));
            return this;
        }

        /// <summary>Sets the member from the index of the object being built (0 for <see cref="Build"/>), for distinct values in <see cref="BuildMany"/>.</summary>
        public Builder<T> With<TMember>(Expression<Func<T, TMember>> member, Func<int, TMember> value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var path = Path(member);
            _steps.Add((target, index) => Set(target!, path, value(index)));
            return this;
        }

        /// <summary>Sets the member to its default (null, 0, false): for "this value is missing" tests.</summary>
        public Builder<T> Without<TMember>(Expression<Func<T, TMember>> member)
        {
            var path = Path(member);
            _steps.Add((target, _) => Set(target!, path, default(TMember)));
            return this;
        }

        /// <summary>Runs any other setup on the object, after the members set so far.</summary>
        public Builder<T> Configure(Action<T> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            _steps.Add((target, _) => configure(target));
            return this;
        }

        /// <summary>Builds one object.</summary>
        public T Build() => BuildMany(1)[0];

        /// <summary>Builds <paramref name="count"/> objects whose sample values are all distinct.</summary>
        public List<T> BuildMany(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var items = _blank
                ? Enumerable.Range(0, count).Select(_ => CreateBlank(typeof(T))).ToList()
                : SampleData.CreateMany(typeof(T), count, _options);
            var result = new List<T>(count);
            for (var i = 0; i < items.Count; i++)
            {
                if (!(items[i] is T item))
                    throw new MapperAssertException("Cannot build a " + typeof(T).Name + "; add a factory with SampleDataOptions.Construct.");
                // Value types are boxed so the steps change the same copy.
                object boxed = item!;
                foreach (var step in _steps) step((T)boxed, i);
                result.Add((T)boxed);
            }
            return result;
        }

        private static object? CreateBlank(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return ctor?.Invoke(null);
        }

        private static List<MemberInfo> Path(LambdaExpression member)
        {
            if (member == null) throw new ArgumentNullException(nameof(member));
            var path = new List<MemberInfo>();
            var body = member.Body is UnaryExpression convert && convert.NodeType == ExpressionType.Convert ? convert.Operand : member.Body;
            while (body is MemberExpression access && (access.Member is PropertyInfo || access.Member is FieldInfo))
            {
                path.Insert(0, access.Member);
                body = access.Expression;
            }
            if (path.Count == 0 || body != member.Parameters[0])
                throw new ArgumentException("Expected a member path of the parameter, like x => x.Address.City; got " + member.Body + ".", nameof(member));
            return path;
        }

        private static void Set(object target, List<MemberInfo> path, object? value)
        {
            var current = target;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var next = Get(current, path[i]);
                if (next == null)
                {
                    next = CreateBlank(MemberType(path[i])) ?? throw new MapperAssertException(
                        "Cannot create " + MemberType(path[i]).Name + " for " + path[i].Name + "; set it first with With(x => x." + path[i].Name + ", ...).");
                    Assign(current, path[i], next);
                }
                // A nested struct is a copy: change it, then write it back.
                if (MemberType(path[i]).IsValueType)
                {
                    Set(next, path.GetRange(i + 1, path.Count - i - 1), value);
                    Assign(current, path[i], next);
                    return;
                }
                current = next;
            }
            Assign(current, path[path.Count - 1], value);
        }

        private static Type MemberType(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        private static object? Get(object target, MemberInfo member) =>
            member is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)member).GetValue(target);

        private static void Assign(object target, MemberInfo member, object? value)
        {
            if (member is FieldInfo field)
            {
                field.SetValue(target, value);
                return;
            }
            var property = (PropertyInfo)member;
            var setter = property.GetSetMethod(nonPublic: true)
                         ?? property.DeclaringType?.GetProperty(property.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(target, new[] { value });
                return;
            }
            // A get-only auto-property: write its backing field.
            var backing = property.DeclaringType?.GetField("<" + property.Name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (backing == null) throw new MapperAssertException(property.DeclaringType?.Name + "." + property.Name + " has no setter.");
            backing.SetValue(target, value);
        }
    }
}
