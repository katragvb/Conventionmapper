using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ConventionMapper.Testing.EntityFrameworkCore
{
    /// <summary>
    /// Checks of mappings into EF Core entities against a real database. Failures throw <see cref="MapperAssertException"/>,
    /// which every test framework reports.
    /// </summary>
    public static partial class DatabaseAssert
    {
        /// <summary>
        /// Compares the EF Core model with the database schema: every mapped table and column exists, and each column's
        /// nullability, maximum length (SQL Server) and whether it has a default agree. The generator's warnings about
        /// lengths, keys and defaults (CM0027, CM0028, CM0034) come from the model, so a model that drifted from the database
        /// makes them wrong. Run it against a database with the real schema (a copy of production), not one created from
        /// the model; without one in CI, use <see cref="ExportSchema"/> once and <see cref="ModelMatchesSchema"/> in tests.
        /// Pass entity types to check only those. Supports SQL Server and SQLite.
        /// </summary>
        public static void ModelMatchesDatabase(DbContext context, params Type[] entityTypes)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var sqlServer = IsSqlServer(context);
            WithConnection(context, connection =>
                Compare(context, entityTypes, sqlServer, lengthsKnown: sqlServer, "the database",
                    (schema, table) => sqlServer ? SqlServerColumns(context, connection, schema!, table) : SqliteColumns(context, connection, table)));
        }

        /// <summary>
        /// Writes the database's schema for the model's tables (columns, nullability, maximum lengths, defaults) to a JSON
        /// file, so <see cref="ModelMatchesSchema"/> can check the model in any test run, without a database. Run it once
        /// against a database with the real schema (a copy of production) and commit the file; run it again after schema changes.
        /// </summary>
        public static void ExportSchema(DbContext context, string path, params Type[] entityTypes)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (path == null) throw new ArgumentNullException(nameof(path));
            var sqlServer = IsSqlServer(context);
            var snapshot = new SchemaSnapshot { Provider = sqlServer ? "SqlServer" : "Sqlite" };
            WithConnection(context, connection =>
            {
                foreach (var (schema, table, _) in Tables(context, entityTypes, sqlServer))
                {
                    var columns = sqlServer ? SqlServerColumns(context, connection, schema!, table) : SqliteColumns(context, connection, table);
                    if (columns == null) continue;
                    snapshot.Tables[Display(schema, table)] = new SortedDictionary<string, DatabaseColumn>(columns, StringComparer.OrdinalIgnoreCase);
                }
            });
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions).Replace("\r\n", "\n") + "\n");
        }

        /// <summary>
        /// Compares the EF Core model with a schema file written by <see cref="ExportSchema"/>: the same checks as
        /// <see cref="ModelMatchesDatabase"/>, without a database, so a scaffolded model that drifted from the real schema
        /// fails every test run. Copy the file to the test output (CopyToOutputDirectory) or pass an absolute path.
        /// </summary>
        public static void ModelMatchesSchema(DbContext context, string path, params Type[] entityTypes)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!File.Exists(path))
                throw new MapperAssertException("No schema file at " + Path.GetFullPath(path) + ". Write it with DatabaseAssert.ExportSchema against a database with the real schema.");
            var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(File.ReadAllText(path), JsonOptions)
                           ?? throw new MapperAssertException("The schema file " + path + " is empty.");
            var tables = new Dictionary<string, SortedDictionary<string, DatabaseColumn>>(snapshot.Tables, StringComparer.OrdinalIgnoreCase);
            var sqlServer = string.Equals(snapshot.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase);
            Compare(context, entityTypes, sqlServer, lengthsKnown: sqlServer, "the schema file",
                (schema, table) => tables.TryGetValue(Display(schema, table), out var columns)
                    ? new Dictionary<string, DatabaseColumn>(columns, StringComparer.OrdinalIgnoreCase)
                    : null);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private sealed class SchemaSnapshot
        {
            public string Provider { get; set; } = "";
            public SortedDictionary<string, SortedDictionary<string, DatabaseColumn>> Tables { get; set; } =
                new SortedDictionary<string, SortedDictionary<string, DatabaseColumn>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DatabaseColumn
        {
            public bool Nullable { get; set; }
            public int? MaxLength { get; set; }
            public int? Precision { get; set; }
            public int? Scale { get; set; }
            public string? Default { get; set; }
        }

        private static readonly System.Text.RegularExpressions.Regex DecimalType =
            new System.Text.RegularExpressions.Regex(@"^\s*(decimal|numeric)\s*\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// A decimal property's precision and scale in the model: HasPrecision, else HasColumnType("decimal(5, 2)") (how
        /// scaffolding writes it), else EF Core's SQL Server default decimal(18, 2).
        /// </summary>
        private static (int Precision, int Scale) ModelPrecision(IProperty property)
        {
            if (property.GetPrecision() is int precision) return (precision, property.GetScale() ?? 0);
            var match = DecimalType.Match(property.GetColumnType() ?? string.Empty);
            if (match.Success)
                return (int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        match.Groups[3].Success ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
            return (18, 2);
        }

        private static bool IsSqlServer(DbContext context)
        {
            var provider = context.Database.ProviderName ?? string.Empty;
            if (provider.EndsWith(".SqlServer", StringComparison.Ordinal)) return true;
            if (provider.EndsWith(".Sqlite", StringComparison.Ordinal)) return false;
            throw new MapperAssertException("DatabaseAssert supports SQL Server and SQLite, not " + provider + ".");
        }

        private static string Display(string? schema, string table) => (schema == null ? string.Empty : schema + ".") + table;

        /// <summary>The model's tables: (schema as the database names it, table, entity type), first entity type per table.</summary>
        private static IEnumerable<(string? Schema, string Table, IEntityType EntityType)> Tables(DbContext context, Type[]? entityTypes, bool sqlServer)
        {
            var only = new HashSet<Type>(entityTypes ?? Type.EmptyTypes);
            var defaultSchema = context.Model.GetDefaultSchema() ?? (sqlServer ? "dbo" : null);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entityType in context.Model.GetEntityTypes().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (only.Count > 0 && !only.Contains(entityType.ClrType)) continue;
                var table = entityType.GetTableName();
                if (table == null) continue; // views, queries, owned types stored as JSON
                var schema = entityType.GetSchema() ?? defaultSchema;
                if (seen.Add(Display(schema, table))) yield return (schema, table, entityType);
            }
        }

        private static void Compare(DbContext context, Type[]? entityTypes, bool sqlServer, bool lengthsKnown, string other,
            Func<string?, string, Dictionary<string, DatabaseColumn>?> columnsOf)
        {
            var only = new HashSet<Type>(entityTypes ?? Type.EmptyTypes);
            var defaultSchema = context.Model.GetDefaultSchema() ?? (sqlServer ? "dbo" : null);
            var problems = new List<string>();
            var checkedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tables = new Dictionary<string, Dictionary<string, DatabaseColumn>?>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityType in context.Model.GetEntityTypes().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (only.Count > 0 && !only.Contains(entityType.ClrType)) continue;
                var table = entityType.GetTableName();
                if (table == null) continue;
                var schema = entityType.GetSchema() ?? defaultSchema;
                var display = Display(schema, table);
                if (!tables.TryGetValue(display, out var columns))
                {
                    columns = columnsOf(schema, table);
                    tables[display] = columns;
                    if (columns == null) problems.Add("Table " + display + " (" + entityType.ClrType.Name + ") does not exist in " + other + ".");
                }
                if (columns == null) continue;

                var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                foreach (var property in entityType.GetProperties())
                {
                    var name = property.GetColumnName(store);
                    if (name == null || !checkedColumns.Add(display + "." + name)) continue;
                    var where = display + "." + name + " (" + entityType.ClrType.Name + "." + property.Name + ")";
                    if (!columns.TryGetValue(name, out var column))
                    {
                        problems.Add(where + ": the column does not exist.");
                        continue;
                    }
                    var modelNullable = property.IsColumnNullable(store);
                    if (modelNullable != column.Nullable)
                        problems.Add(where + ": " + (modelNullable ? "the model allows null, the database does not." : "the database allows null, the model does not."));
                    if (lengthsKnown && (property.ClrType == typeof(string) || property.ClrType == typeof(byte[])))
                    {
                        var modelLength = property.GetMaxLength();
                        if (modelLength != column.MaxLength)
                            problems.Add(where + ": maximum length " + Length(modelLength) + " in the model, " + Length(column.MaxLength) + " in the database.");
                    }
                    if (lengthsKnown && (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(decimal) && column.Precision != null)
                    {
                        // A smaller scale in the database rounds what the map writes (12.5 stored as 13).
                        var (precision, scale) = ModelPrecision(property);
                        if (precision != column.Precision || scale != (column.Scale ?? 0))
                            problems.Add(where + ": decimal(" + precision + ", " + scale + ") in the model, decimal(" + column.Precision + ", " +
                                         (column.Scale ?? 0) + ") in the database.");
                    }
                    var modelDefault = property.GetDefaultValueSql() != null || property.FindAnnotation(RelationalAnnotationNames.DefaultValue) != null;
                    if (modelDefault != (column.Default != null))
                        problems.Add(where + ": " + (modelDefault
                            ? "the model has a default value, the database column has none."
                            : "the database column has the default " + column.Default + ", the model does not know it."));
                }
            }

            if (problems.Count > 0)
                throw new MapperAssertException("The EF Core model does not match " + other + ":\n  " + string.Join("\n  ", problems));
        }

        private static string Length(int? length) => length == null ? "unlimited" : length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static Dictionary<string, DatabaseColumn>? SqlServerColumns(DbContext context, DbConnection connection, string schema, string table)
        {
            using var command = Command(context, connection,
                "SELECT COLUMN_NAME, IS_NULLABLE, CHARACTER_MAXIMUM_LENGTH, COLUMN_DEFAULT, DATA_TYPE, NUMERIC_PRECISION, NUMERIC_SCALE " +
                "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table");
            Parameter(command, "@schema", schema);
            Parameter(command, "@table", table);
            var columns = new Dictionary<string, DatabaseColumn>(StringComparer.OrdinalIgnoreCase);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var length = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
                    var isDecimal = reader.GetString(4) is var type && (string.Equals(type, "decimal", StringComparison.OrdinalIgnoreCase) ||
                                                                       string.Equals(type, "numeric", StringComparison.OrdinalIgnoreCase));
                    columns[reader.GetString(0)] = new DatabaseColumn
                    {
                        Nullable = string.Equals(reader.GetString(1), "YES", StringComparison.OrdinalIgnoreCase),
                        MaxLength = length == -1 ? null : length, // (max)
                        Precision = isDecimal && !reader.IsDBNull(5) ? Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture) : (int?)null,
                        Scale = isDecimal && !reader.IsDBNull(6) ? Convert.ToInt32(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture) : (int?)null,
                        Default = reader.IsDBNull(3) ? null : reader.GetString(3),
                    };
                }
            return columns.Count == 0 ? null : columns;
        }

        private static Dictionary<string, DatabaseColumn>? SqliteColumns(DbContext context, DbConnection connection, string table)
        {
            using var command = Command(context, connection, "SELECT name, \"notnull\", dflt_value, pk FROM pragma_table_info(@table)");
            Parameter(command, "@table", table);
            var columns = new Dictionary<string, DatabaseColumn>(StringComparer.OrdinalIgnoreCase);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    columns[reader.GetString(0)] = new DatabaseColumn
                    {
                        // SQLite keys never hold null, whether or not they are declared NOT NULL.
                        Nullable = reader.GetInt64(1) == 0 && reader.GetInt64(3) == 0,
                        Default = reader.IsDBNull(2) ? null : reader.GetString(2),
                    };
            return columns.Count == 0 ? null : columns;
        }

        private static DbCommand Command(DbContext context, DbConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            // A test that wraps its work in a transaction (and rolls it back) must run its commands inside it.
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            return command;
        }

        private static void Parameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static void WithConnection(DbContext context, Action<DbConnection> action)
        {
            var connection = context.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened) context.Database.OpenConnection();
            try
            {
                action(connection);
            }
            finally
            {
                if (opened) context.Database.CloseConnection();
            }
        }
    }
}
