namespace ProductOrderApi.Domain;

public sealed class Product
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }

    public ICollection<OrderItem> OrderItems { get; set; } = [];
}
