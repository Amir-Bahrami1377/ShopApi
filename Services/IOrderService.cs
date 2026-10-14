using ProductOrderApi.Dtos;

namespace ProductOrderApi.Services;

public interface IOrderService
{
    Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, string? idempotencyKey, CancellationToken cancellationToken);
    Task<OrderResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<OrderResponse> ConfirmAsync(Guid id, CancellationToken cancellationToken);
    Task<OrderResponse> CancelAsync(Guid id, CancellationToken cancellationToken);
}
