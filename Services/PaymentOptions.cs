namespace ProductOrderApi.Services;

public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    public PaymentMode Mode { get; set; } = PaymentMode.Success;
    public int ArtificialDelayMilliseconds { get; set; } = 100;
    public int TimeoutSeconds { get; set; } = 5;
}

public enum PaymentMode
{
    Success,
    Declined,
    Error,
    Timeout,
    Random
}
