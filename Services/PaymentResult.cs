namespace ProductOrderApi.Services;

public enum PaymentOutcome
{
    Succeeded,
    Declined,
    Error
}

public sealed record PaymentResult(PaymentOutcome Outcome, string? Error = null)
{
    public static PaymentResult Success() => new(PaymentOutcome.Succeeded);
    public static PaymentResult Declined(string? reason = null) => new(PaymentOutcome.Declined, reason);
    public static PaymentResult Failed(string? error = null) => new(PaymentOutcome.Error, error);
}
