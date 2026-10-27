#if NET8_0_OR_GREATER
using System;
using System.Linq;
using ConventionMapper.Testing;
using ConventionMapper.Testing.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConventionMapper.Tests.TestingPackage
{
    public class DbForm
    {
        public int Id { get; set; }
        public bool PrintText { get; set; }
        public bool? Optional { get; set; }
        public string Name { get; set; } = "";
        public int QuoteRef { get; set; }
    }

    public class DbQuoteForm
    {
        public bool PrintText { get; set; }
        public bool? Optional { get; set; }
        public string Name { get; set; } = "";
    }

    public class FormContext : DbContext
    {
        private readonly SqliteConnection _connection;
        public FormContext(SqliteConnection connection) => _connection = connection;
        public DbSet<DbForm> Forms => Set<DbForm>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(_connection);
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<DbForm>(entity =>
            {
                entity.Property(e => e.PrintText).HasDefaultValueSql("1");
                entity.Property(e => e.Optional).HasDefaultValueSql("1");
                entity.Property(e => e.Name).HasMaxLength(10);
            });
    }

    /// <summary>ConventionMapper.Testing.EntityFrameworkCore against an in-memory SQLite database.</summary>
    public sealed class DatabaseAssertTests : IDisposable
    {
        private readonly SqliteConnection _connection = new SqliteConnection("DataSource=:memory:");

        public DatabaseAssertTests() => _connection.Open();

        public void Dispose() => _connection.Dispose();

        private FormContext Created()
        {
            var context = new FormContext(_connection);
            context.Database.EnsureCreated();
            return context;
        }

        private static DbForm Copy(DbQuoteForm source) =>
            new DbForm { PrintText = source.PrintText, Optional = source.Optional, Name = source.Name };

        [Fact]
        public void A_database_default_that_replaces_a_copied_false_fails()
        {
            using var context = Created();

            var error = Assert.Throws<MapperAssertException>(() =>
                DatabaseAssert.SavesRoundTrip<DbQuoteForm, DbForm>(context, Copy, new DbQuoteForm { PrintText = false, Optional = false, Name = "A" }));

            // bool? sends every value it holds, so Optional keeps its false.
            Assert.Contains("DbForm.PrintText: the map wrote false, the database holds true", error.Message);
            Assert.Contains("CM0034", error.Message);
            Assert.DoesNotContain("Optional", error.Message);
        }

        [Fact]
        public void Values_the_database_keeps_pass_and_nothing_stays_saved()
        {
            using var context = Created();

            var stored = DatabaseAssert.SavesRoundTrip<DbQuoteForm, DbForm>(context, Copy, new DbQuoteForm { PrintText = true, Optional = false, Name = "A" });

            Assert.True(stored.PrintText);
            Assert.Equal(0, context.Forms.Count()); // rolled back
        }

        [Fact]
        public void Sample_data_is_used_when_no_source_is_given()
        {
            using var context = Created();

            // SampleData fills bools with true, so the default never matters here.
            var stored = DatabaseAssert.SavesRoundTrip<DbQuoteForm, DbForm>(context, Copy);

            Assert.True(stored.PrintText);
            Assert.StartsWith("Name", stored.Name);
        }

        [Fact]
        public void A_model_created_database_matches_its_model()
        {
            using var context = Created();

            DatabaseAssert.ModelMatchesDatabase(context);
        }

        [Fact]
        public void Schema_drift_is_reported_column_by_column()
        {
            using var context = new FormContext(_connection);
            // The real table: no default on PrintText, Optional NOT NULL, a default the model doesn't know, no QuoteRef.
            context.Database.ExecuteSqlRaw(
                "CREATE TABLE Forms (Id INTEGER NOT NULL PRIMARY KEY, PrintText INTEGER NOT NULL, Optional INTEGER NOT NULL DEFAULT 1, Name TEXT NOT NULL DEFAULT 'x')");

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesDatabase(context));

            Assert.Contains("Forms.PrintText (DbForm.PrintText): the model has a default value, the database column has none.", error.Message);
            Assert.Contains("Forms.Optional (DbForm.Optional): the model allows null, the database does not.", error.Message);
            Assert.Contains("Forms.Name (DbForm.Name): the database column has the default 'x', the model does not know it.", error.Message);
            Assert.Contains("Forms.QuoteRef (DbForm.QuoteRef): the column does not exist.", error.Message);
        }

        [Fact]
        public void A_missing_table_is_reported()
        {
            using var context = new FormContext(_connection);

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesDatabase(context, typeof(DbForm)));

            Assert.Contains("Table Forms (DbForm) does not exist in the database.", error.Message);
        }

        [Fact]
        public void A_schema_file_checks_the_model_without_a_database()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-schema-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                // Exported once from the "real" database (here: one that drifted from the model), then committed.
                using (var real = new FormContext(_connection))
                {
                    real.Database.ExecuteSqlRaw(
                        "CREATE TABLE Forms (Id INTEGER NOT NULL PRIMARY KEY, PrintText INTEGER NOT NULL DEFAULT 1, Optional INTEGER NOT NULL DEFAULT 1, Name TEXT NOT NULL, QuoteRef INTEGER NOT NULL)");
                    DatabaseAssert.ExportSchema(real, path);
                }
                Assert.Contains("\"Optional\"", System.IO.File.ReadAllText(path));

                // Any later test run: only the model and the file.
                using var other = new SqliteConnection("DataSource=:memory:");
                using var model = new FormContext(other);
                var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesSchema(model, path));

                Assert.Contains("does not match the schema file", error.Message);
                Assert.Contains("Forms.Optional (DbForm.Optional): the model allows null, the database does not.", error.Message);
                Assert.DoesNotContain("PrintText", error.Message);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void Decimal_precision_and_scale_are_compared_on_SQL_Server()
        {
            // A SQL Server schema file: the model says decimal(5, 2), the database rounds to whole numbers.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-schema-" + Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(path, @"{
  ""provider"": ""SqlServer"",
  ""tables"": {
    ""dbo.Prices"": {
      ""Amount"": { ""nullable"": false, ""precision"": 5, ""scale"": 0 },
      ""Fee"": { ""nullable"": false, ""precision"": 18, ""scale"": 2 },
      ""Id"": { ""nullable"": false },
      ""Rate"": { ""nullable"": false, ""precision"": 9, ""scale"": 4 }
    }
  }
}");
            try
            {
                using var model = new PriceContext(_connection);
                var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesSchema(model, path));

                Assert.Contains("dbo.Prices.Amount (DbPrice.Amount): decimal(5, 2) in the model, decimal(5, 0) in the database.", error.Message);
                Assert.DoesNotContain("Fee", error.Message);   // EF Core's default decimal(18, 2)
                Assert.DoesNotContain("Rate", error.Message);  // HasPrecision(9, 4)
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void A_schema_file_of_a_matching_database_passes()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-schema-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (var real = Created()) DatabaseAssert.ExportSchema(real, path);
                using var model = new FormContext(new SqliteConnection("DataSource=:memory:"));

                DatabaseAssert.ModelMatchesSchema(model, path);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void A_missing_schema_file_says_how_to_create_it()
        {
            using var model = new FormContext(_connection);

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.ModelMatchesSchema(model, "missing-schema.json"));

            Assert.Contains("DatabaseAssert.ExportSchema", error.Message);
        }

        [Fact]
        public void Every_map_into_the_database_round_trips_with_sample_data_and_with_false_and_0()
        {
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON";
                pragma.ExecuteNonQuery();
            }
            using var context = new NoteContext(_connection);
            context.Database.EnsureCreated();

            var error = Assert.Throws<MapperAssertException>(() => DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(DbMappings)));

            // ToOwner passes both runs; ToNote passes with sample data (Done is true) but loses a copied false. Its
            // OwnerRef points at no owner: foreign keys aren't checked, so no rows need seeding. ToTag's key includes a
            // foreign key, which keeps a non-zero value in the false-and-0 run (EF Core can't save a key holding 0).
            Assert.StartsWith("1 of 6 runs of 3 maps into the database don't round-trip:", error.Message);
            Assert.Contains("ToNote(DbNoteInput) with false and 0 values: After saving and reading back, these values changed:", error.Message);
            Assert.Contains("DbNote.Done: the map wrote false, the database holds true", error.Message);
            Assert.DoesNotContain("FOREIGN KEY", error.Message);
            Assert.Equal(0, context.Notes.Count());
        }

        [Fact]
        public void A_skipped_map_is_left_out()
        {
            using var context = new NoteContext(_connection);
            context.Database.EnsureCreated();

            DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(DbMappings), skip: new[] { nameof(DbMappings.ToNote) });
        }

        [Fact]
        public void Inside_the_tests_own_transaction_each_run_is_rolled_back_to_a_savepoint()
        {
            using var context = new NoteContext(_connection);
            context.Database.EnsureCreated();
            using var transaction = context.Database.BeginTransaction();

            // Sample data repeats its values, so ToTag's key is the same both times: the first run's row must be gone.
            DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(DbMappings), skip: new[] { nameof(DbMappings.ToNote) });
            DatabaseAssert.AllMappersSaveRoundTrip(context, typeof(DbMappings), skip: new[] { nameof(DbMappings.ToNote) });

            Assert.Equal(0, context.Tags.Count());
            Assert.Same(transaction, context.Database.CurrentTransaction);
        }
    }

    public class DbOwner { public int Id { get; set; } public string Name { get; set; } = ""; }
    public class DbOwnerInput { public string Name { get; set; } = ""; }
    public class DbNote { public int Id { get; set; } public int OwnerRef { get; set; } public DbOwner? Owner { get; set; } public string Title { get; set; } = ""; public bool Done { get; set; } }
    public class DbNoteInput { public string Title { get; set; } = ""; public bool Done { get; set; } }

    public class NoteContext : DbContext
    {
        private readonly SqliteConnection _connection;
        public NoteContext(SqliteConnection connection) => _connection = connection;
        public DbSet<DbOwner> Owners => Set<DbOwner>();
        public DbSet<DbNote> Notes => Set<DbNote>();
        public DbSet<DbTag> Tags => Set<DbTag>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(_connection);
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DbNote>(entity =>
            {
                entity.Property(e => e.Title).HasMaxLength(5);
                entity.Property(e => e.Done).HasDefaultValueSql("1");
                entity.HasOne(e => e.Owner).WithMany().HasForeignKey(e => e.OwnerRef);
            });
            // A key that is also a foreign key: EF Core can't save it while it holds 0, whatever the map wrote.
            modelBuilder.Entity<DbTag>(entity =>
            {
                entity.HasKey(e => new { e.NoteRef, e.Number });
                entity.HasOne(e => e.Note).WithMany().HasForeignKey(e => e.NoteRef);
            });
        }
    }

    public class DbPrice { public int Id { get; set; } public decimal Amount { get; set; } public decimal Fee { get; set; } public decimal Rate { get; set; } }

    public class PriceContext : DbContext
    {
        private readonly SqliteConnection _connection;
        public PriceContext(SqliteConnection connection) => _connection = connection;
        public DbSet<DbPrice> Prices => Set<DbPrice>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(_connection);
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<DbPrice>(entity =>
            {
                entity.Property(e => e.Amount).HasColumnType("decimal(5, 2)");
                entity.Property(e => e.Rate).HasPrecision(9, 4);
            });
    }

    public class DbTag { public int NoteRef { get; set; } public int Number { get; set; } public DbNote? Note { get; set; } public string Label { get; set; } = ""; }
    public class DbTagInput { public string Label { get; set; } = ""; }

    [ConventionMapper.GeneratedMapper]
    public static partial class DbMappings
    {
        [ConventionMapper.MapIgnore("Id")]
        public static partial DbOwner ToOwner(this DbOwnerInput input);

        // Acknowledged at build time on purpose: the round trip shows what the acknowledgement hides.
        [ConventionMapper.MapIgnore("Id"), ConventionMapper.MapIgnore("OwnerRef"), ConventionMapper.MapIgnore("Owner")]
        [ConventionMapper.MapReviewed("Done", "Title", For = ConventionMapper.ReviewKind.DatabaseDefault | ConventionMapper.ReviewKind.Length)]
        public static partial DbNote ToNote(this DbNoteInput input);

        [ConventionMapper.MapIgnore("Note")]
        public static partial DbTag ToTag(this DbTagInput input, int noteRef, int number);
    }
}
#endif
