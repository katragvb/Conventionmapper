using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ConventionMapper.Testing.EntityFrameworkCore
{
    public static partial class DatabaseAssert
    {
        /// <summary>
        /// Maps <paramref name="source"/> (sample data when omitted) to a new entity, inserts it, reads it back from the
        /// database and fails for every column whose stored value differs from what the map wrote: a database default that
        /// replaced a copied false or 0 (CM0034) or null (CM0036), text the database cut, numbers it rounded. A failed insert
        /// (too long for the column, a missing foreign key row) fails with the database's message. Runs inside a transaction
        /// that is rolled back, or inside the test's own transaction when one is open. Returns the entity as stored.
        /// </summary>
        /// <param name="context">A context on a database with the real schema (or one created from the model).</param>
        /// <param name="map">The mapping method under test.</param>
        /// <param name="source">The input; <see cref="SampleData.Create{T}()"/> when omitted.</param>
        /// <param name="prepare">Runs before the insert, for values the map leaves to the caller (foreign keys of seeded rows).</param>
        /// <param name="ignore">Members not to compare (timestamps the database sets, for example).</param>
        public static TEntity SavesRoundTrip<TSource, TEntity>(DbContext context, Func<TSource, TEntity> map, TSource? source = default,
            Action<TEntity>? prepare = null, IEnumerable<string>? ignore = null)
            where TEntity : class
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (map == null) throw new ArgumentNullException(nameof(map));
            var entity = map(source ?? SampleData.Create<TSource>());
            prepare?.Invoke(entity);
            return (TEntity)InTransaction(context, () => RoundTrip(context, entity, Skipped(ignore)));
        }

        /// <summary>
        /// <see cref="SavesRoundTrip"/> for every generated create method of <paramref name="mapperType"/> whose result is an
        /// entity of <paramref name="context"/>, with sample data for every parameter, so each map into the database is checked
        /// without a test of its own. Foreign keys are not checked inside the rolled-back transaction (no rows need seeding);
        /// sample strings are cut to the column's maximum length (lengths are CM0027's job), sample decimals are rounded to its scale, and xml columns get a valid
        /// document. Lists every failing method. Pass <paramref name="options"/> for values sample data can't make up (codes
        /// a check constraint allows), and <paramref name="skip"/> for methods to leave out.
        /// </summary>
        public static void AllMappersSaveRoundTrip(DbContext context, Type mapperType, SampleDataOptions? options = null, IEnumerable<string>? skip = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (mapperType == null) throw new ArgumentNullException(nameof(mapperType));
            var skipped = new HashSet<string>(skip ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var methods = mapperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<GeneratedCodeAttribute>()?.Tool == "ConventionMapper.SourceGenerator" &&
                            m.ReturnType != typeof(void) && m.GetParameters().Length > 0 && !m.IsGenericMethod &&
                            context.Model.FindEntityType(m.ReturnType) != null && !skipped.Contains(m.Name))
                .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GetParameters().Length)
                .ToList();
            if (methods.Count == 0)
                throw new MapperAssertException("'" + mapperType.FullName + "' has no generated method that creates an entity of " + context.GetType().Name + ".");

            // Sample data never holds false or 0, the values a database default replaces (CM0034), so every map also runs
            // with those.
            var zeros = (options?.Clone() ?? new SampleDataOptions())
                .For<bool>(() => false).For<byte>(() => 0).For<short>(() => 0).For<int>(() => 0).For<long>(() => 0L)
                .For<decimal>(() => 0m).For<double>(() => 0d).For<float>(() => 0f);
            var failures = new List<string>();
            var nextKey = 1000;
            foreach (var (method, pass) in methods.SelectMany(m => new[] { (m, options), (m, zeros) }))
            {
                var signature = method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")" +
                                (ReferenceEquals(pass, zeros) ? " with false and 0 values" : string.Empty);
                try
                {
                    var arguments = method.GetParameters().Select(p => SampleData.Create(p.ParameterType, pass)).ToArray();
                    var entity = method.Invoke(null, arguments)
                                 ?? throw new MapperAssertException("it returned null for sample data.");
                    var entityType = context.Model.FindEntityType(entity.GetType())!;
                    FitSampleValues(entityType, entity, () => nextKey++);
                    InTransaction(context, () =>
                    {
                        SkipForeignKeyChecks(context, entityType);
                        return RoundTrip(context, entity, new HashSet<string>(StringComparer.Ordinal));
                    });
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    failures.Add(signature + ": the map threw " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
                }
                catch (MapperAssertException e)
                {
                    failures.Add(signature + ": " + e.Message.Replace("\n", "\n    "));
                }
                catch (Exception e)
                {
                    // One map's problem must not hide the others'.
                    failures.Add(signature + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
            if (failures.Count > 0)
                throw new MapperAssertException(failures.Count + " of " + (methods.Count * 2) + " runs of " + methods.Count +
                                                " maps into the database don't round-trip:\n  " + string.Join("\n  ", failures));
        }

        private static HashSet<string> Skipped(IEnumerable<string>? ignore) => new HashSet<string>(ignore ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        /// <summary>
        /// Runs <paramref name="body"/> in a transaction of its own that is rolled back, or, inside the test's open transaction,
        /// up to a savepoint that is rolled back, so nothing it saves (or the constraints it switches off) stays behind and two
        /// runs with the same sample keys don't collide.
        /// </summary>
        private static object InTransaction(DbContext context, Func<object> body)
        {
            var outer = context.Database.CurrentTransaction;
            var ownTransaction = outer == null ? context.Database.BeginTransaction() : null;
            var savepoint = outer != null && outer.SupportsSavepoints ? "ConventionMapperRoundTrip" : null;
            if (savepoint != null) outer!.CreateSavepoint(savepoint);
            try
            {
                return body();
            }
            finally
            {
                if (ownTransaction != null)
                {
                    ownTransaction.Rollback();
                    ownTransaction.Dispose();
                }
                else if (savepoint != null)
                {
                    outer!.RollbackToSavepoint(savepoint);
                }
                context.ChangeTracker.Clear();
            }
        }

        /// <summary>Inserts the entity, reads it back with a clean change tracker and compares every value the map wrote.</summary>
        private static object RoundTrip(DbContext context, object entity, ISet<string> skipped)
        {
            var name = entity.GetType().Name;
            var entry = context.Add(entity);
            // What the map (and prepare) wrote, before SaveChanges copies database-generated values into the entity.
            var written = entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue, StringComparer.Ordinal);
            try
            {
                context.SaveChanges();
            }
            catch (DbUpdateException e)
            {
                var inner = e.InnerException ?? e;
                throw new MapperAssertException("Saving the mapped " + name + " failed: " + inner.Message, e);
            }
            catch (InvalidOperationException e)
            {
                // EF Core refuses before reaching the database (a key holding its CLR default, a missing required navigation).
                throw new MapperAssertException("EF Core can't save the mapped " + name + ": " + e.Message, e);
            }

            var key = entry.Metadata.FindPrimaryKey()
                      ?? throw new MapperAssertException(name + " has no primary key, so it can't be read back.");
            var keyValues = key.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
            context.ChangeTracker.Clear();
            var stored = context.Find(entity.GetType(), keyValues)
                         ?? throw new MapperAssertException("The saved " + name + " could not be read back.");

            var problems = new List<string>();
            foreach (var property in context.Entry(stored).Properties)
            {
                var metadata = property.Metadata;
                if (skipped.Contains(metadata.Name) || !written.TryGetValue(metadata.Name, out var expected)) continue;
                // Values the database owns: generated keys, computed columns, row versions.
                if (metadata.ValueGenerated == ValueGenerated.OnAddOrUpdate || metadata.ValueGenerated == ValueGenerated.OnUpdate) continue;
                if (metadata.IsPrimaryKey() && metadata.ValueGenerated == ValueGenerated.OnAdd) continue;
                var actual = property.CurrentValue;
                if (SameValue(metadata, expected, actual)) continue;
                problems.Add(name + "." + metadata.Name + ": the map wrote " + Show(expected) + ", the database holds " +
                             Show(actual) + Explain(metadata, expected, actual));
            }
            if (problems.Count > 0)
                throw new MapperAssertException("After saving and reading back, these values changed:\n  " + string.Join("\n  ", problems));
            return stored;
        }

        /// <summary>
        /// Sample strings carry the member name, so they can be longer than a column; sample decimals are rounded to the
        /// column's scale; xml columns need a document; and a key
        /// member holding 0 (the false-and-0 run) gets a unique value, because EF Core can't save a key it treats as not set.
        /// </summary>
        private static void FitSampleValues(IEntityType entityType, object entity, Func<int> nextKey)
        {
            foreach (var property in entityType.FindPrimaryKey()?.Properties ?? Array.Empty<IProperty>())
            {
                if (property.PropertyInfo == null || !property.PropertyInfo.CanWrite || property.ValueGenerated != ValueGenerated.Never) continue;
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type != typeof(int) && type != typeof(long) && type != typeof(short) && type != typeof(byte)) continue;
                var value = property.PropertyInfo.GetValue(entity);
                if (value != null && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0) continue;
                property.PropertyInfo.SetValue(entity, Convert.ChangeType(nextKey() % (type == typeof(byte) ? 250 : int.MaxValue) + 1, type, CultureInfo.InvariantCulture));
            }
            foreach (var property in entityType.GetProperties())
            {
                // Sample decimals have digits after the point that a decimal(5, 0) column never receives from real data.
                if (property.PropertyInfo != null && property.PropertyInfo.CanWrite &&
                    (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(decimal) &&
                    property.PropertyInfo.GetValue(entity) is decimal number)
                    property.PropertyInfo.SetValue(entity, Math.Round(number, ModelPrecision(property).Scale));
                if (property.PropertyInfo == null || property.ClrType != typeof(string) || !property.PropertyInfo.CanWrite) continue;
                if (!(property.PropertyInfo.GetValue(entity) is string text)) continue;
                if (string.Equals(property.GetColumnType(), "xml", StringComparison.OrdinalIgnoreCase))
                    property.PropertyInfo.SetValue(entity, "<sample />");
                else if (property.GetMaxLength() is int length && text.Length > length)
                    property.PropertyInfo.SetValue(entity, text.Substring(0, length));
            }
        }

        /// <summary>Foreign keys are checked at commit (SQLite) or not at all (SQL Server) inside the rolled-back transaction.</summary>
        private static void SkipForeignKeyChecks(DbContext context, IEntityType entityType)
        {
            if (IsSqlServer(context))
            {
                var table = entityType.GetTableName();
                if (table == null) return;
                var schema = entityType.GetSchema() ?? context.Model.GetDefaultSchema() ?? "dbo";
                context.Database.ExecuteSqlRaw("ALTER TABLE " + Quote(schema) + "." + Quote(table) + " NOCHECK CONSTRAINT ALL");
            }
            else
            {
                context.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys = ON");
            }
        }

        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

        private static bool SameValue(IProperty property, object? expected, object? actual)
        {
            if (Equals(expected, actual)) return true;
            if (expected == null || actual == null) return false;
            switch (expected)
            {
                case string text when actual is string other:
                    // Fixed-length columns (char(n)) pad with spaces.
                    return property.IsFixedLength() == true && string.Equals(text.TrimEnd(), other.TrimEnd(), StringComparison.Ordinal);
                case byte[] bytes when actual is byte[] otherBytes:
                    return bytes.SequenceEqual(otherBytes);
                case DateTime time when actual is DateTime otherTime:
                    // SQL Server datetime keeps 1/300 of a second.
                    return Math.Abs((time - otherTime).TotalMilliseconds) < 4;
                case DateTimeOffset offset when actual is DateTimeOffset otherOffset:
                    return Math.Abs((offset - otherOffset).TotalMilliseconds) < 4;
            }
            return false;
        }

        private static string Explain(IProperty property, object? expected, object? actual)
        {
            var hasDefault = property.GetDefaultValueSql() != null || property.FindAnnotation(RelationalAnnotationNames.DefaultValue) != null;
            var clrDefault = property.ClrType.IsValueType && Nullable.GetUnderlyingType(property.ClrType) == null ? Activator.CreateInstance(property.ClrType) : null;
            if (hasDefault && Equals(expected, clrDefault))
                return expected == null
                    ? " (the column has a database default: EF Core leaves null out of the insert, so the default applied; see CM0036)."
                    : " (the column has a database default: EF Core leaves a property holding its CLR default out of the insert, so the default applied; see CM0034).";
            if (expected is string text && actual is string stored && text.StartsWith(stored, StringComparison.Ordinal))
                return " (the database cut the text to " + stored.Length.ToString(CultureInfo.InvariantCulture) + " characters).";
            return ".";
        }

        private static string Show(object? value) => value switch
        {
            null => "null",
            string text => "\"" + text + "\"",
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }
}
