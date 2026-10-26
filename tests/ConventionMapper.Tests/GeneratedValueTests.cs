using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using ConventionMapper;
#if NET8_0_OR_GREATER
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
#endif

// End-to-end: [MapConstant], [MapFrom], [MapRequired], [MapTruncate] and projections with parameters, compiled by the
// generator in this project and run (projections also against SQLite).
namespace ConventionMapper.Tests.Values
{
    public enum VStatus { Unknown, New, Unchanged }

    public class VProgram
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public Guid? ContextId { get; set; }
        public string Manuscript { get; set; } = "0";
        public string? Code { get; set; }
    }

    public class VRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        [MaxLength(5)] public string City { get; set; } = "";
        public Guid ContextId { get; set; }
        public bool Manuscript { get; set; }
        public string Code { get; set; } = "";
        public int PolicyTypeCode { get; set; }
        public VStatus Status { get; set; }
    }

    public class VProgramData
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsSelected { get; set; }
    }

    [GeneratedMapper]
    public static partial class VMapper
    {
        [MapConstant(nameof(VRow.PolicyTypeCode), 1)]
        [MapConstant(nameof(VRow.Status), VStatus.New)]
        [MapFrom(nameof(IsManuscript), nameof(VRow.Manuscript))]
        [MapFrom(nameof(ContextOrNew), nameof(VRow.ContextId))]
        [MapTruncate(nameof(VRow.City))]
        [MapRequired(nameof(VRow.Code))]
        public static partial VRow ToRow(VProgram program);

        private static bool IsManuscript(VProgram p) => p.Manuscript == "1";

        private static Guid ContextOrNew(VProgram p) => p.ContextId ?? Guid.NewGuid();

        // A projection with a query parameter; Selected is inlined, so it runs in SQL.
        [MapFrom(nameof(Selected), nameof(VProgramData.IsSelected))]
        [MapTruncate(nameof(VProgramData.Name), 3)]
        public static partial IQueryable<VProgramData> ProjectToProgramData(this IQueryable<VProgram> query, int selectedProgramId);

        private static bool Selected(VProgram p, int selectedProgramId) => p.Id == selectedProgramId;
    }

    public class GeneratedValueTests
    {
        private static VProgram Program(int id = 7) => new VProgram
        {
            Id = id, Name = "Downtown", City = "Cincinnati", ContextId = null, Manuscript = "1", Code = "C-1",
        };

        [Fact]
        public void Constants_computed_values_and_truncation_are_applied()
        {
            var row = VMapper.ToRow(Program());

            Assert.Equal(1, row.PolicyTypeCode);
            Assert.Equal(VStatus.New, row.Status);
            Assert.True(row.Manuscript);
            Assert.NotEqual(Guid.Empty, row.ContextId);      // ContextId ?? Guid.NewGuid()
            Assert.Equal("Cinci", row.City);                 // [MaxLength(5)]
            Assert.Equal("C-1", row.Code);

            var known = Guid.NewGuid();
            var existing = Program();
            existing.ContextId = known;
            Assert.Equal(known, VMapper.ToRow(existing).ContextId);
        }

        [Fact]
        public void A_required_member_without_a_value_throws_a_descriptive_exception()
        {
            var program = Program();
            program.Code = null;

            var error = Assert.Throws<InvalidOperationException>(() => VMapper.ToRow(program));

            Assert.Equal("ToRow: VRow.Code is required, but VProgram.Code is null.", error.Message);
        }

        [Fact]
        public void Projection_with_a_parameter_runs_in_memory()
        {
            var data = new[] { Program(1), Program(2) }.AsQueryable().ProjectToProgramData(selectedProgramId: 2).ToList();

            Assert.False(data[0].IsSelected);
            Assert.True(data[1].IsSelected);
            Assert.Equal("Dow", data[0].Name);
        }

#if NET8_0_OR_GREATER
        private sealed class ProgramContext : DbContext
        {
            public ProgramContext(DbContextOptions<ProgramContext> options) : base(options) { }
            public DbSet<VProgram> Programs => Set<VProgram>();
        }

        [Fact]
        public void EF_Core_sends_the_parameter_and_runs_the_inlined_method_in_SQL()
        {
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<ProgramContext>().UseSqlite(connection).Options;
            using (var db = new ProgramContext(options))
            {
                db.Database.EnsureCreated();
                db.Programs.AddRange(Program(1), Program(2));
                db.SaveChanges();
            }

            using (var db = new ProgramContext(options))
            {
                var query = db.Programs.OrderBy(p => p.Id).ProjectToProgramData(2);
                var sql = query.ToQueryString();
                Assert.Contains("@__selectedProgramId", sql);      // a query parameter, not a constant
                Assert.Contains("substr", sql);                     // truncation in SQL
                Assert.DoesNotContain("Manuscript", sql);           // only the needed columns

                var data = query.ToList();
                Assert.False(data[0].IsSelected);
                Assert.True(data[1].IsSelected);
                Assert.Equal("Dow", data[1].Name);
            }
        }
#endif
    }
}
