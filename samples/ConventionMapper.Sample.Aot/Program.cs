// Native AOT sample: every mapping below is generated as plain C# at build time.
// Run:  dotnet publish -c Release -r linux-x64 -o out && ./out/ConventionMapper.Sample.Aot
using System.Globalization;
using ConventionMapper;

var order = new Order
{
    Id = 42,
    Customer = new Customer { Name = "Ada", Address = new Address { City = "London" } },
    Status = OrderStatus.Shipped,
    Total = new Money { Amount = 19.5m, Currency = "GBP" },
    Lines =
    {
        new OrderLine { Sku = "PEN", Quantity = 2 },
        new OrderLine { Sku = "INK", Quantity = 1 }
    },
    Tags = { ["gift"] = 1 }
};

var dto = order.ToDto();
var summary = OrderMapper.ToSummary(order);

var checks = new (string Name, bool Passed)[]
{
    ("flattening", dto.CustomerName == "Ada" && dto.CustomerAddressCity == "London"),
    ("enum by name", dto.Status == OrderStatusDto.Shipped),
    ("collection", dto.Lines.Length == 2 && dto.Lines[1].Sku == "INK"),
    ("dictionary", dto.Tags["gift"] == 1L),
    ("conversion method", dto.Total == "19.50 GBP"),
    ("record constructor", summary == new OrderSummary(42, "Ada", 2)),
};

foreach (var check in checks) Console.WriteLine((check.Passed ? "ok    " : "FAILED") + " " + check.Name);
Console.WriteLine($"Order {dto.Id} for {dto.CustomerName} ({dto.CustomerAddressCity}): {dto.Lines.Length} lines, {dto.Total}");
return checks.All(c => c.Passed) ? 0 : 1;

public enum OrderStatus { Pending, Shipped }
public enum OrderStatusDto { Pending, Shipped }

public class Address { public string City { get; set; } = ""; }
public class Customer { public string Name { get; set; } = ""; public Address? Address { get; set; } }
public class Money { public decimal Amount { get; set; } public string Currency { get; set; } = ""; }
public class OrderLine { public string Sku { get; set; } = ""; public int Quantity { get; set; } }

public class Order
{
    public int Id { get; set; }
    public Customer? Customer { get; set; }
    public OrderStatus Status { get; set; }
    public Money Total { get; set; } = new();
    public List<OrderLine> Lines { get; } = new();
    public Dictionary<string, int> Tags { get; } = new();
    public int GetLineCount() => Lines.Count;
}

public class OrderLineDto { public string Sku { get; set; } = ""; public int Quantity { get; set; } }

public class OrderDto
{
    public int Id { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddressCity { get; set; }
    public OrderStatusDto Status { get; set; }
    public string Total { get; set; } = "";
    public OrderLineDto[] Lines { get; set; } = [];
    public Dictionary<string, long> Tags { get; set; } = new();
}

public record OrderSummary(int Id, string? CustomerName, int LineCount);

[GeneratedMapper]
public static partial class OrderMapper
{
    public static partial OrderDto ToDto(this Order order);

    public static partial OrderSummary ToSummary(Order order);

    private static string Format(Money money) => money.Amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + money.Currency;
}
