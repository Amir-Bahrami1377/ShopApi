namespace ProductOrderApi.Domain;

public sealed class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }

    public ICollection<OrderItem> Items { get; set; } = [];
}
