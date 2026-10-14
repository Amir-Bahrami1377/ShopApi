namespace ProductOrderApi.Domain;

public enum OrderStatus
{
    Pending,
    PaymentProcessing,
    Confirmed,
    Cancelled
}
