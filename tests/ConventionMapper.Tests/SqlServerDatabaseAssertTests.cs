#if NET8_0_OR_GREATER
using System;
using System.Linq;
using ConventionMapper.Testing;
using ConventionMapper.Testing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ConventionMapper.Tests.TestingPackage
{
    /// <summary>A test that needs a real SQL Server: skipped unless CONVENTIONMAPPER_SQLSERVER holds a connection string.</summary>
    public sealed class SqlServerFactAttribute : FactAttribute
    {
        public const string Variable = "CONVENTIONMAPPER_SQLSERVER";

        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = "Set " + Variable + " to a SQL Server connection string (the sqlserver CI job does).";
        }
    }

    public class SqlOwner { public int Id { get; set; } public string Name { get; set; } = ""; }

    public class SqlForm
    {
        public int Id { get; set; }
        public bool PrintText { get; set; }
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public int OwnerRef { get; set; }
        public SqlOwner? Owner { get; set; }
    }

    public class SqlFormInput { public bool PrintText { get; set; } public string Name { get; set; } = ""; public decimal Amount { get; set; } public int OwnerRef { get; set; } }

    public class SqlFormContext : DbContext
    {
        private readonly string _connectionString;
        public SqlFormContext(string connectionString) => _connectionString = connectionString;
        public DbSet<SqlForm> Forms => Set<SqlForm>();
        public DbSet<SqlOwner> Owners => Set<SqlOwner>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlServer(_connectionString);
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<SqlForm>(entity =>
            {
                entity.ToTable("Forms", t => t.HasCheckConstraint("CK_Forms_Amount", "[Amount] > 0"));
                entity.Property(e => e.PrintText).HasDefaultValueSql("((1))");
                entity.Property(e => e.Name).HasMaxLength(10);
                entity.Property(e => e.Amount).HasPrecision(9, 2);
                entity.HasOne(e => e.Owner).WithMany().HasForeignKey(e => e.OwnerRef);
            });
    }

    /// <summary>ConventionMapper.Testing.EntityFrameworkCore against a real SQL Server (its own database, dropped afterwards).</summary>
    public sealed class SqlServerDatabaseAssertTests : IDisposable
    {
        private readonly string? _connectionString;

        public SqlServerDatabaseAssertTests()
        {
            var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable);
            if (string.IsNullOrWhiteSpace(server)) return;
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server) { InitialCatalog = "cm_" + Guid.NewGuid().ToString("N") };
            _connectionString = builder.ConnectionString;
            using var context = new SqlFormContext(_connectionString);
            context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            if (_connectionString == null) return;
            using var context = new SqlFormContext(_connectionString);
            context.Database.EnsureDeleted();
        }

        private SqlFormContext Context() => new SqlFormContext(_connectionString!);

        /// <summary>A committed owner row for SavesRoundTrip, which checks foreign keys.</summary>
        private int SeedOwner()
        {
            using var context = Context();
            var owner = new SqlOwner { Name = "O" };
            context.Owners.Add(owner);
            context.SaveChanges();
            return owner.Id;
        }

        private static SqlForm Copy(SqlFormInput input) =>
            new SqlForm { PrintText = input.PrintText, Name = input.Name, Amount = input.Amount, OwnerRef = input.OwnerRef };

        private int DisabledForeignKeys()
        {
            using var context = Context();
            return context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE is_disabled = 1").AsEnumerable().Single();
        }

        [SqlServerFact]
        public void A_database_default_that_replaces_a_copied_false_fails()
        {
            var owner = SeedOwner();
            using var context = Context();
            var error = Assert.Throws<MapperAssertException>(() =>
                DatabaseAssert.SavesRoundTrip<SqlFormInput, SqlForm>(context, Copy, new SqlFormInput { PrintText = false, Name = "A", Amount = 1, OwnerRef = owner }));
            Assert.Contains("SqlForm.PrintText: the map wrote false, the database holds true", error.Message);
            Assert.Equal(0, context.Forms.Count()); // rolled back
        }

        [SqlServerFact]
        public void SavesRoundTrip_reports_what_the_database_rejects()
        {
            var owner = SeedOwner();
            using var context = Context();

            // SavesRoundTrip checks foreign keys: a missing row fails with the database's message.
            var missing = Assert.ThrowsAny<Exception>(() =>
                DatabaseAssert.SavesRoundTrip<SqlFormInput, SqlForm>(context, Copy, new SqlFormInput { PrintText = true, Name = "A", Amount = 1, OwnerRef = owner + 1000 }));
            Assert.Contains("FOREIGN KEY", missing.ToString());

            var rejected = Assert.ThrowsAny<Exception>(() =>
                DatabaseAssert.SavesRoundTrip<SqlFormInput, SqlForm>(context, Copy, new SqlFormInput { PrintText = true, Name = "A", Amount = -1, OwnerRef = owner }));
            Assert.Contains("CK_Forms_Amount", rejected.ToString());

            var stored = DatabaseAssert.SavesRoundTrip<SqlFormInput, SqlForm>(context, Copy, new SqlFormInput { PrintText = true, Name = "A", Amount = 12.34m, OwnerRef = owner });
            Assert.Equal(12.34m, stored.Amount);
        }

        [SqlServerFact]
        public void AllMappersSaveRoundTrip_skips_only_foreign_keys()
        {
            using var context = Context();

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(SqlMappings)));

            // Sample data points OwnerRef at no owner: foreign keys are switched off, so that run passes...
            Assert.DoesNotContain("FOREIGN KEY", error.ToString());
            Assert.StartsWith("1 of 2 runs of 1 maps into the database don't round-trip:", error.Message);
            // ...but the CHECK constraint stays on: the false-and-0 run writes Amount = 0, which CK_Forms_Amount rejects.
            Assert.Contains("ToForm(SqlFormInput) with false and 0 values", error.Message);
            Assert.Contains("CK_Forms_Amount", error.Message);
            Assert.Equal(0, context.Forms.Count());
            Assert.Equal(0, DisabledForeignKeys()); // NOCHECK was rolled back with the transaction
        }

        [SqlServerFact]
        public void A_model_created_database_matches_its_model()
        {
            using var context = Context();
            DatabaseAssert.ModelMatchesDatabase(context);
        }

        [SqlServerFact]
        public void Schema_drift_is_reported_column_by_column()
        {
            using var context = Context();
            context.Database.ExecuteSqlRaw("ALTER TABLE dbo.Forms ALTER COLUMN Name nvarchar(20) NULL");
            context.Database.ExecuteSqlRaw("ALTER TABLE dbo.Forms ALTER COLUMN Amount decimal(9, 0) NOT NULL");
            context.Database.ExecuteSqlRaw("DECLARE @c sysname = (SELECT name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.Forms') AND COL_NAME(parent_object_id, parent_column_id) = 'PrintText'); EXEC('ALTER TABLE dbo.Forms DROP CONSTRAINT ' + @c)");

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesDatabase(context));

            Assert.Contains("dbo.Forms.Name (SqlForm.Name): the database allows null, the model does not.", error.Message);
            Assert.Contains("dbo.Forms.Name (SqlForm.Name): maximum length 10 in the model, 20 in the database.", error.Message);
            Assert.Contains("dbo.Forms.Amount (SqlForm.Amount): decimal(9, 2) in the model, decimal(9, 0) in the database.", error.Message);
            Assert.Contains("dbo.Forms.PrintText (SqlForm.PrintText): the model has a default value, the database column has none.", error.Message);
        }

        [SqlServerFact]
        public void An_exported_schema_checks_the_model_without_the_database()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-sql-schema-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (var context = Context()) DatabaseAssert.ExportSchema(context, path);
                Assert.Contains("\"SqlServer\"", System.IO.File.ReadAllText(path));
                using var model = new SqlFormContext("Server=nowhere;Database=none"); // never opened
                DatabaseAssert.ModelMatchesSchema(model, path);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }

    [GeneratedMapper]
    public static partial class SqlMappings
    {
        [MapIgnore(nameof(SqlForm.Id)), MapIgnore(nameof(SqlForm.Owner)), MapReviewed(nameof(SqlForm.PrintText)), MapTruncate(nameof(SqlForm.Name))]
        public static partial SqlForm ToForm(SqlFormInput input);
    }
}
#endif
