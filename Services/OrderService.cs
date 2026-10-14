using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ProductOrderApi.Data;
using ProductOrderApi.Domain;
using ProductOrderApi.Dtos;
using ProductOrderApi.Errors;

namespace ProductOrderApi.Services;

public sealed class OrderService(
    AppDbContext dbContext,
    IPaymentService paymentService,
    IOptions<PaymentOptions> paymentOptions,
    ILogger<OrderService> logger) : IOrderService
{
    private const int MaximumOrderLines = 100;
    private const int MaximumIdempotencyKeyLength = 200;

    public async Task<CreateOrderResult> CreateAsync(
        CreateOrderRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var normalizedLines = NormalizeLines(request);
        idempotencyKey = ValidateIdempotencyKey(idempotencyKey);
        var requestHash = ComputeRequestHash(normalizedLines);

        if (idempotencyKey is not null)
        {
            var existing = await FindIdempotentOrderAsync(idempotencyKey, requestHash, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var productIds = normalizedLines.Select(line => line.ProductId).ToArray();
            var prices = await dbContext.Products
                .AsNoTracking()
                .Where(product => productIds.Contains(product.Id))
                .ToDictionaryAsync(product => product.Id, product => product.Price, cancellationToken);

            var missingProductId = productIds.FirstOrDefault(productId => !prices.ContainsKey(productId));
            if (missingProductId != Guid.Empty)
            {
                throw AppException.NotFound($"Product '{missingProductId}' was not found.");
            }

            foreach (var line in normalizedLines)
            {
                var affectedRows = await dbContext.Products
                    .Where(product => product.Id == line.ProductId && product.Stock >= line.Quantity)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            product => product.Stock,
                            product => product.Stock - line.Quantity),
                        cancellationToken);

                if (affectedRows == 0)
                {
                    throw AppException.Conflict($"Product '{line.ProductId}' does not have enough stock.");
                }
            }

            var order = new Order
            {
                Id = Guid.NewGuid(),
                Status = OrderStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = idempotencyKey,
                RequestHash = idempotencyKey is null ? null : requestHash
            };

            foreach (var line in normalizedLines)
            {
                order.Items.Add(new OrderItem
                {
                    OrderId = order.Id,
                    ProductId = line.ProductId,
                    Quantity = line.Quantity,
                    UnitPrice = prices[line.ProductId],
                    Order = order
                });
            }

            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Created order {OrderId} with {LineCount} lines.", order.Id, order.Items.Count);
            return new CreateOrderResult(Map(order), false);
        }
        catch (AppException exception) when (
            exception.StatusCode == StatusCodes.Status409Conflict && idempotencyKey is not null)
        {
            await RollbackSafelyAsync(transaction);
            dbContext.ChangeTracker.Clear();

            var replay = await FindIdempotentOrderAsync(idempotencyKey, requestHash, CancellationToken.None);
            if (replay is not null)
            {
                return replay;
            }

            throw;
        }
        catch (DbUpdateException exception) when (
            idempotencyKey is not null && IsUniqueConstraintViolation(exception))
        {
            await RollbackSafelyAsync(transaction);
            dbContext.ChangeTracker.Clear();

            var replay = await FindIdempotentOrderAsync(idempotencyKey, requestHash, CancellationToken.None);
            if (replay is not null)
            {
                return replay;
            }

            throw AppException.Conflict(
                "A concurrent request is using the same Idempotency-Key. Retry the request.",
                exception);
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
    }

    public async Task<OrderResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(id, cancellationToken);
        return order is null
            ? throw AppException.NotFound($"Order '{id}' was not found.")
            : Map(order);
    }

    public async Task<OrderResponse> ConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        var claimedRows = await dbContext.Orders
            .Where(order => order.Id == id && order.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(order => order.Status, OrderStatus.PaymentProcessing),
                cancellationToken);

        if (claimedRows == 0)
        {
            return await ResolveFailedConfirmationClaimAsync(id, cancellationToken);
        }

        Order claimedOrder;
        try
        {
            claimedOrder = await LoadOrderAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Claimed order '{id}' disappeared before payment.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetPaymentStatusAsync(id);
            throw;
        }
        catch
        {
            await ResetPaymentStatusAsync(id);
            throw;
        }

        var total = CalculateTotal(claimedOrder);

        using var paymentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        paymentCancellation.CancelAfter(TimeSpan.FromSeconds(paymentOptions.Value.TimeoutSeconds));

        PaymentResult result;
        try
        {
            result = await paymentService.ChargeAsync(id, total, paymentCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetPaymentStatusAsync(id);
            logger.LogInformation("Payment for order {OrderId} was interrupted because the client disconnected.", id);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await ResetPaymentStatusAsync(id);
            throw AppException.PaymentTimeout(exception);
        }
        catch (Exception exception)
        {
            await ResetPaymentStatusAsync(id);
            throw AppException.PaymentProviderError(exception);
        }

        switch (result.Outcome)
        {
            case PaymentOutcome.Succeeded:
                var finalizedRows = await dbContext.Orders
                    .Where(order => order.Id == id && order.Status == OrderStatus.PaymentProcessing)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(order => order.Status, OrderStatus.Confirmed),
                        CancellationToken.None);
                if (finalizedRows != 1)
                {
                    logger.LogCritical("Payment succeeded for order {OrderId}, but its status could not be finalized.", id);
                    throw new InvalidOperationException("A paid order could not be finalized.");
                }

                logger.LogInformation("Confirmed order {OrderId}.", id);
                return await GetByIdAsync(id, CancellationToken.None);

            case PaymentOutcome.Declined:
                await ResetPaymentStatusAsync(id);
                throw AppException.PaymentDeclined(result.Error);

            case PaymentOutcome.Error:
            default:
                await ResetPaymentStatusAsync(id);
                throw AppException.PaymentProviderError(diagnosticReason: result.Error);
        }
    }

    public async Task<OrderResponse> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var cancelledRows = await dbContext.Orders
                .Where(order => order.Id == id && order.Status == OrderStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(order => order.Status, OrderStatus.Cancelled),
                    cancellationToken);

            if (cancelledRows == 0)
            {
                var existingOrder = await LoadOrderAsync(id, cancellationToken);
                if (existingOrder is null)
                {
                    throw AppException.NotFound($"Order '{id}' was not found.");
                }

                if (existingOrder.Status == OrderStatus.Cancelled)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return Map(existingOrder);
                }

                throw AppException.Conflict(
                    $"Order '{id}' cannot be cancelled while its status is {existingOrder.Status}.");
            }

            var items = await dbContext.OrderItems
                .AsNoTracking()
                .Where(item => item.OrderId == id)
                .OrderBy(item => item.ProductId)
                .ToListAsync(cancellationToken);

            foreach (var item in items)
            {
                var restoredRows = await dbContext.Products
                    .Where(product => product.Id == item.ProductId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            product => product.Stock,
                            product => product.Stock + item.Quantity),
                        cancellationToken);
                if (restoredRows != 1)
                {
                    throw new InvalidOperationException(
                        $"Could not restore stock for product '{item.ProductId}' while cancelling order '{id}'.");
                }
            }

            var cancelledOrder = await LoadOrderAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Cancelled order '{id}' disappeared.");
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Cancelled order {OrderId} and restored its reserved stock.", id);
            return Map(cancelledOrder);
        }
        catch
        {
            await RollbackSafelyAsync(transaction);
            throw;
        }
    }

    private async Task<OrderResponse> ResolveFailedConfirmationClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await LoadOrderAsync(id, cancellationToken);
        if (order is null)
        {
            throw AppException.NotFound($"Order '{id}' was not found.");
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            return Map(order);
        }

        throw AppException.Conflict($"Order '{id}' cannot be confirmed while its status is {order.Status}.");
    }

    private async Task ResetPaymentStatusAsync(Guid orderId)
    {
        try
        {
            var resetRows = await dbContext.Orders
                .Where(order => order.Id == orderId && order.Status == OrderStatus.PaymentProcessing)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(order => order.Status, OrderStatus.Pending),
                    CancellationToken.None);
            if (resetRows != 1)
            {
                logger.LogCritical("Could not reset payment status to Pending for order {OrderId}.", orderId);
            }
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Could not reset payment status to Pending for order {OrderId}.", orderId);
        }
    }

    private async Task<CreateOrderResult?> FindIdempotentOrderAsync(
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.IdempotencyKey == idempotencyKey, cancellationToken);
        if (order is null)
        {
            return null;
        }

        if (!string.Equals(order.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw AppException.IdempotencyConflict();
        }

        logger.LogInformation("Replayed idempotent order {OrderId}.", order.Id);
        return new CreateOrderResult(Map(order), true);
    }

    private Task<Order?> LoadOrderAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Orders
            .AsNoTracking()
            .Include(order => order.Items)
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);

    private static IReadOnlyList<NormalizedOrderLine> NormalizeLines(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var items = request.Items;
        if (items is null || items.Count == 0)
        {
            errors["items"] = ["At least one order item is required."];
        }
        else if (items.Count > MaximumOrderLines)
        {
            errors["items"] = [$"An order cannot contain more than {MaximumOrderLines} lines."];
        }

        if (errors.Count > 0)
        {
            throw AppException.Validation(errors);
        }

        var quantities = new Dictionary<Guid, long>();
        for (var index = 0; index < items!.Count; index++)
        {
            var item = items[index];
            if (item is null)
            {
                errors[$"items[{index}]"] = ["Order item must not be null."];
                continue;
            }

            if (item.ProductId == Guid.Empty)
            {
                errors[$"items[{index}].productId"] = ["Product id must not be empty."];
            }

            if (item.Quantity < 1)
            {
                errors[$"items[{index}].quantity"] = ["Quantity must be at least 1."];
            }

            if (item.ProductId != Guid.Empty && item.Quantity > 0)
            {
                quantities.TryGetValue(item.ProductId, out var currentQuantity);
                var combinedQuantity = currentQuantity + item.Quantity;
                if (combinedQuantity > int.MaxValue)
                {
                    errors[$"items[{index}].quantity"] = ["Combined quantity for a product is too large."];
                }
                else
                {
                    quantities[item.ProductId] = combinedQuantity;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw AppException.Validation(errors);
        }

        return quantities
            .OrderBy(pair => pair.Key)
            .Select(pair => new NormalizedOrderLine(pair.Key, checked((int)pair.Value)))
            .ToArray();
    }

    private static string? ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (idempotencyKey is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Idempotency-Key must not be empty or whitespace."]
            });
        }

        if (idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            throw AppException.Validation(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = [$"Idempotency-Key cannot exceed {MaximumIdempotencyKeyLength} characters."]
            });
        }

        return idempotencyKey;
    }

    private static string ComputeRequestHash(IEnumerable<NormalizedOrderLine> lines)
    {
        var normalized = string.Join(';', lines.Select(line => $"{line.ProductId:N}:{line.Quantity}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && sqlException.Number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RollbackSafelyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            logger.LogError(rollbackException, "Failed to roll back a database transaction.");
        }
    }

    private static decimal CalculateTotal(Order order) =>
        order.Items.Sum(item => item.UnitPrice * item.Quantity);

    private static OrderResponse Map(Order order)
    {
        var items = order.Items
            .OrderBy(item => item.ProductId)
            .Select(item => new OrderItemResponse(item.ProductId, item.Quantity, item.UnitPrice))
            .ToArray();
        return new OrderResponse(
            order.Id,
            order.Status.ToString(),
            order.CreatedAt,
            items.Sum(item => item.UnitPrice * item.Quantity),
            items);
    }

    private sealed record NormalizedOrderLine(Guid ProductId, int Quantity);
}
