namespace ProductOrderApi.Services;

public interface IPaymentService
{
    Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken);
}
