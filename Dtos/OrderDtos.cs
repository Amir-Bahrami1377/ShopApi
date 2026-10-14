using System.ComponentModel.DataAnnotations;

namespace ProductOrderApi.Dtos;

public sealed record CreateOrderRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public required IReadOnlyList<CreateOrderItemRequest> Items { get; init; }
}

public sealed record CreateOrderItemRequest
{
    public Guid ProductId { get; init; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; init; }
}

public sealed record OrderItemResponse(Guid ProductId, int Quantity, decimal UnitPrice);

public sealed record OrderResponse(
    Guid Id,
    string Status,
    DateTimeOffset CreatedAt,
    decimal Total,
    IReadOnlyList<OrderItemResponse> Items);

public sealed record CreateOrderResult(OrderResponse Order, bool Replayed);
