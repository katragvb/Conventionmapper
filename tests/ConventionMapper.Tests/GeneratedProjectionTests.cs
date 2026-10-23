using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using ConventionMapper;
#if NET8_0_OR_GREATER
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
#endif

namespace ConventionMapper.Tests.Projection
{
    public enum PStatus { Open, Shipped }
    public enum PStatusDto { Open, Shipped }

    public class PAddress { public int Id { get; set; } public string City { get; set; } = ""; }

    public class PCustomer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int? AddressId { get; set; }
        public PAddress? Address { get; set; }
    }

    public class PLine { public int Id { get; set; } public int OrderId { get; set; } public string Sku { get; set; } = ""; public int Qty { get; set; } }

    public class POrder
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public PCustomer Customer { get; set; } = null!;
        public List<PLine> Lines { get; set; } = new List<PLine>();
        public PStatus Status { get; set; }
        public int? Priority { get; set; }
        public string InternalNotes { get; set; } = "";
    }

    public class PCustomerDto { public string Name { get; set; } = ""; }
    public class PLineDto { public string Sku { get; set; } = ""; public int Qty { get; set; } }

    public class POrderDto
    {
        public int Id { get; set; }
        public string CustomerName { get; set; } = "";
        public string? CustomerAddressCity { get; set; }
        public PCustomerDto? Customer { get; set; }
        public PLineDto[] Lines { get; set; } = Array.Empty<PLineDto>();
        public PStatusDto Status { get; set; }
        public int Priority { get; set; }
    }

    public record POrderSummary(int Id, string CustomerName, int LinesCount);

    [GeneratedMapper]
    public static partial class PMapper
    {
        public static partial IQueryable<POrderDto> ProjectToDto(this IQueryable<POrder> query);

        public static partial Expression<Func<POrder, POrderSummary>> SummaryProjection();
    }

    public class GeneratedProjectionTests
    {
        private static List<POrder> Orders() => new List<POrder>
        {
            new POrder
            {
                Id = 1, CustomerId = 1, Status = PStatus.Shipped, Priority = 2, InternalNotes = "secret",
                Customer = new PCustomer { Id = 1, Name = "Ada", Address = new PAddress { Id = 1, City = "London" } },
                Lines = { new PLine { Id = 1, Sku = "PEN", Qty = 2 }, new PLine { Id = 2, Sku = "INK", Qty = 1 } }
            },
            new POrder
            {
                Id = 2, CustomerId = 2, Status = PStatus.Open, Priority = null,
                Customer = new PCustomer { Id = 2, Name = "Grace", Address = new PAddress { Id = 2, City = "Arlington" } }
            }
        };

        [Fact]
        public void Projection_runs_in_memory_with_the_same_results_as_the_mapping()
        {
            var dtos = Orders().AsQueryable().ProjectToDto().ToList();

            Assert.Equal(2, dtos.Count);
            Assert.Equal("Ada", dtos[0].CustomerName);
            Assert.Equal("London", dtos[0].CustomerAddressCity);
            Assert.Equal("Ada", dtos[0].Customer!.Name);
            Assert.Equal(new[] { "PEN", "INK" }, dtos[0].Lines.Select(l => l.Sku));
            Assert.Equal(PStatusDto.Shipped, dtos[0].Status);
            Assert.Equal(2, dtos[0].Priority);
            Assert.Equal(0, dtos[1].Priority);
            Assert.Empty(dtos[1].Lines);
        }

        [Fact]
        public void Expression_projection_is_a_plain_expression_tree()
        {
            var expression = PMapper.SummaryProjection();

            Assert.IsAssignableFrom<NewExpression>(expression.Body); // record constructor, no helper calls
            Assert.Same(expression, PMapper.SummaryProjection()); // cached, so LINQ providers can cache the query too
            var summaries = Orders().AsQueryable().Select(expression).ToList();
            Assert.Equal(new POrderSummary(1, "Ada", 2), summaries[0]);
        }

#if NET8_0_OR_GREATER
        private sealed class ShopContext : DbContext
        {
            public ShopContext(DbContextOptions<ShopContext> options) : base(options) { }
            public DbSet<POrder> Orders => Set<POrder>();
            public DbSet<PCustomer> Customers => Set<PCustomer>();
        }

        [Fact]
        public void EF_Core_translates_generated_projections_to_SQL()
        {
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).Options;

            using (var db = new ShopContext(options))
            {
                db.Database.EnsureCreated();
                var orders = Orders();
                orders[1].Customer.Address = null; // NULL navigation: handled by SQL, not by the generated code
                db.Orders.AddRange(orders);
                db.SaveChanges();
            }

            using (var db = new ShopContext(options))
            {
                var query = db.Orders.OrderBy(o => o.Id).ProjectToDto();
                var sql = query.ToQueryString();
                Assert.DoesNotContain("InternalNotes", sql); // only the columns the DTO needs

                var dtos = query.ToList();
                Assert.Equal("London", dtos[0].CustomerAddressCity);
                Assert.Null(dtos[1].CustomerAddressCity);
                Assert.Equal(new[] { "PEN", "INK" }, dtos[0].Lines.Select(l => l.Sku));
                Assert.Equal(PStatusDto.Open, dtos[1].Status);
                Assert.Equal(0, dtos[1].Priority);

                var summaries = db.Orders.OrderBy(o => o.Id).Select(PMapper.SummaryProjection()).ToList();
                Assert.Equal(new POrderSummary(2, "Grace", 0), summaries[1]);
            }
        }
#endif
    }
}
