using System.Collections.Concurrent;
using ProductOrderApi.Services;

namespace ProductOrderApi.IntegrationTests;

internal sealed class ControllablePaymentService : IPaymentService
{
    private readonly ConcurrentQueue<PaymentCall> _calls = new();
    private readonly object _behaviorLock = new();
    private Func<Guid, decimal, CancellationToken, Task<PaymentResult>> _behavior =
        static (_, _, _) => Task.FromResult(PaymentResult.Success());

    public IReadOnlyCollection<PaymentCall> Calls => _calls.ToArray();

    public Task<PaymentResult> ChargeAsync(
        Guid orderId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        _calls.Enqueue(new PaymentCall(orderId, amount));

        Func<Guid, decimal, CancellationToken, Task<PaymentResult>> behavior;
        lock (_behaviorLock)
        {
            behavior = _behavior;
        }

        return behavior(orderId, amount, cancellationToken);
    }

    public void Succeed() => SetBehavior(
        static (_, _, _) => Task.FromResult(PaymentResult.Success()));

    public void Decline(string error = "Card declined") => SetBehavior(
        (_, _, _) => Task.FromResult(PaymentResult.Declined(error)));

    public void Fail(string error = "Payment provider unavailable") => SetBehavior(
        (_, _, _) => Task.FromResult(PaymentResult.Failed(error)));

    public void TimeOut() => SetBehavior(
        static async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PaymentResult.Success();
        });

    public BlockingPaymentGate BlockFirstCallThenSucceed(bool ignoreCancellation = false)
    {
        var gate = new BlockingPaymentGate(ignoreCancellation);
        SetBehavior(gate.HandleAsync);
        return gate;
    }

    public void ClearCalls()
    {
        while (_calls.TryDequeue(out _))
        {
        }
    }

    private void SetBehavior(
        Func<Guid, decimal, CancellationToken, Task<PaymentResult>> behavior)
    {
        lock (_behaviorLock)
        {
            _behavior = behavior;
        }
    }
}

internal sealed record PaymentCall(Guid OrderId, decimal Amount);

internal sealed class BlockingPaymentGate(bool ignoreCancellation)
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancellationObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public Task Entered => _entered.Task;

    public Task CancellationObserved => _cancellationObserved.Task;

    public void Release() => _release.TrySetResult();

    internal async Task<PaymentResult> HandleAsync(
        Guid orderId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
        {
            _entered.TrySetResult();
            using var cancellationRegistration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            if (ignoreCancellation)
            {
                await _release.Task;
            }
            else
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
        }

        return PaymentResult.Success();
    }
}
