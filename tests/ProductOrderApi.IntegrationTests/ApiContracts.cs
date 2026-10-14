namespace ProductOrderApi.IntegrationTests;

internal sealed record CreateProductRequest(string Name, decimal Price, int Stock);

internal sealed record ProductResponse(Guid Id, string Name, decimal Price, int Stock);

internal sealed record CreateOrderRequest(IReadOnlyCollection<CreateOrderItemRequest> Items);

internal sealed record CreateOrderItemRequest(Guid ProductId, int Quantity);

internal sealed record OrderResponse(
    Guid Id,
    string Status,
    DateTimeOffset CreatedAt,
    decimal Total,
    IReadOnlyCollection<OrderItemResponse> Items);

internal sealed record OrderItemResponse(Guid ProductId, int Quantity, decimal UnitPrice);
