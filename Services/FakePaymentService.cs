using Microsoft.Extensions.Options;

namespace ProductOrderApi.Services;

public sealed class FakePaymentService(
    IOptionsMonitor<PaymentOptions> options,
    ILogger<FakePaymentService> logger) : IPaymentService
{
    public async Task<PaymentResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var mode = settings.Mode == PaymentMode.Random
            ? (PaymentMode)Random.Shared.Next((int)PaymentMode.Success, (int)PaymentMode.Timeout + 1)
            : settings.Mode;

        logger.LogInformation(
            "Fake payment attempt for order {OrderId}, amount {Amount}, mode {PaymentMode}.",
            orderId,
            amount,
            mode);

        if (mode == PaymentMode.Timeout)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (settings.ArtificialDelayMilliseconds > 0)
        {
            await Task.Delay(settings.ArtificialDelayMilliseconds, cancellationToken);
        }

        return mode switch
        {
            PaymentMode.Success => PaymentResult.Success(),
            PaymentMode.Declined => PaymentResult.Declined("The fake payment provider declined the payment."),
            PaymentMode.Error => PaymentResult.Failed("The fake payment provider returned an error."),
            _ => PaymentResult.Failed("The fake payment provider returned an unsupported result.")
        };
    }
}
