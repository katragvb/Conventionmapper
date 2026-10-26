using ConventionMapper;

namespace BuildIntegration
{
    /// <summary>An order.</summary>
    public class Order
    {
        /// <summary>Id.</summary>
        public int Id { get; set; }

        /// <summary>Name.</summary>
        public string Name { get; set; } = "";
    }

    /// <summary>An order DTO.</summary>
    public class OrderDto
    {
        /// <summary>Id.</summary>
        public int Id { get; set; }

        /// <summary>Title.</summary>
        public string Title { get; set; } = "";
    }

    /// <summary>Order mapper.</summary>
    [GeneratedMapper]
    public static partial class OrderMapper
    {
        /// <summary>Maps an order.</summary>
        [MapMember("Name", "Title")]
        public static partial OrderDto ToDto(this Order order);
    }
}
